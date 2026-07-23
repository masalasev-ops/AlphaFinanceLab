using System.Text.Json.Nodes;
using AlphaLab.Data;
using AlphaLab.Data.Entities;

namespace AlphaLab.Evaluation.Candidates;

/// <summary>The frozen definition of a new candidate strategy (D17). config_json + exit_policy_json are
/// immutable once created — a change forks a new strategy_id.</summary>
public sealed record CandidateSpec(
    string StrategyId, string Family, string ConfigJson, string ExitPolicyJson, int? HoldingHorizonDays, string? ParentStrategyId = null);

/// <summary>
/// The D52 pre-registration factory (rule 16). A candidate may be created ONLY with a linked, immutable
/// (locked) hypothesis — a claim + metric + evidence window fixed BEFORE any evidence is seen — OR with an
/// explicit 'unregistered' marker written into strategies.config_json (rendered permanently on the card,
/// so an unregistered candidate can never masquerade as pre-registered). Every creation increments
/// trials_registry (the honest deflated-Sharpe count, D17/S2). Writes via the caller's transaction (D59).
/// </summary>
public sealed class CandidateFactory(AlphaLabDbContext db, AlphaLab.Core.Config.GateOptions? gate = null)
{
    /// <summary>The config_json property that flags an unregistered candidate (rule 16).</summary>
    public const string UnregisteredMarkerKey = "unregistered";

    /// <summary>Pre-register a hypothesis (journal_entries kind='hypothesis'), LOCKED immediately — a
    /// pre-registration is immutable except via the outcome-closure flow (D52). Returns its entry_id.
    /// <paramref name="expectedEffectAnn"/> is the D89 FOURTH pre-declared field (annualized fraction)
    /// the FR-40 gate reads at candidate creation; the API requires it on new hypotheses — the null
    /// default exists only for hypotheses locked before M5, which bypass the gate as legacy.</summary>
    public long RegisterHypothesis(string createdOn, string title, string bodyMd, string metric, int evidenceWindowDays,
        string? strategyId = null, double? expectedEffectAnn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        var row = new JournalEntryRow
        {
            CreatedOn = createdOn,
            Kind = "hypothesis",
            Title = title,
            BodyMd = bodyMd,
            Metric = metric,
            EvidenceWindowDays = evidenceWindowDays,
            StrategyId = strategyId,
            Locked = true,
            ExpectedEffectAnn = expectedEffectAnn,
        };
        db.JournalEntries.Add(row);
        db.SaveChanges();
        return row.EntryId;
    }

    /// <summary>
    /// Create a candidate. FR-28: fails if NEITHER a linked locked hypothesis NOR the unregistered flag is
    /// supplied. Registers a trials_registry row (kind new|fork|retrain|sibling) and links the hypothesis.
    /// </summary>
    public StrategyRow CreateCandidate(
        CandidateSpec spec, long? hypothesisEntryId, bool unregistered, string createdOn,
        string trialKind = "new", string runKind = "live")
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.StrategyId);

        // The 'plant:' prefix is RESERVED for the D64 calibration fixtures (Phase-4 review): a real
        // candidate named into it would be invisible on every plant-filtered screen while its
        // strategies/trials/hypothesis rows persist unremovably, its live trials row would inflate the
        // S2 deflation count for every real strategy — and a later with-plants replay whose seeded id
        // collided would silently adopt the forward row as a fixture. Refuse at the door.
        if (Calibration.PlantCohorts.IsPlantId(spec.StrategyId))
        {
            throw new ArgumentException(
                $"Strategy id '{spec.StrategyId}' uses the reserved 'plant:' prefix — plant ids belong to the " +
                "D64 calibration fixtures and are never admissible candidates.", nameof(spec));
        }

        // The pre-registration gate (D52/rule 16).
        if (hypothesisEntryId is null && !unregistered)
        {
            throw new InvalidOperationException(
                "CandidateFactory (D52/rule 16): a candidate requires a linked pre-registered hypothesis " +
                "OR an explicit 'unregistered' flag — neither was supplied.");
        }

        JournalEntryRow? hypothesis = null;
        if (hypothesisEntryId is { } hid)
        {
            hypothesis = db.JournalEntries.FirstOrDefault(j => j.EntryId == hid && j.Kind == "hypothesis")
                ?? throw new InvalidOperationException($"Hypothesis entry {hid} not found (or not a 'hypothesis').");
            if (!hypothesis.Locked)
            {
                throw new InvalidOperationException(
                    $"Hypothesis entry {hid} is not locked — a pre-registration must be immutable before it can back a candidate (D52).");
            }
            // A pre-registration backs EXACTLY ONE candidate (D52/rule 16). If it is already linked, reusing
            // its entry_id would silently create a second candidate claiming the frozen claim/metric of the
            // first — reject it rather than skip the link (the link guard below would otherwise pass silently).
            if (hypothesis.StrategyId is not null)
            {
                throw new InvalidOperationException(
                    $"Hypothesis entry {hid} is already linked to strategy '{hypothesis.StrategyId}' — " +
                    "a pre-registration backs exactly one candidate (D52/rule 16).");
            }
        }

        if (db.Strategies.Any(s => s.StrategyId == spec.StrategyId))
            throw new InvalidOperationException($"Strategy '{spec.StrategyId}' already exists (frozen identity, D17).");

        // The FR-40/D89 detectability-at-admission gate (Phase 4): a REGISTERED candidate whose
        // pre-declared expected effect cannot clear the detection floor within the horizon is refused
        // BEFORE any row is written (a DetectabilityRefusedException — the API's 422
        // `detectability_refused`). An UNREGISTERED candidate has no expected_effect_ann and bypasses
        // under its permanent marking; a hypothesis locked before M5 (null field) bypasses as legacy;
        // a factory constructed without GateOptions (pre-Phase-4 call sites, tests) leaves the gate
        // unassessed. Admission-only — a live strategy is never re-gated (rule 8).
        if (gate is not null && hypothesis?.ExpectedEffectAnn is { } expectedEffectAnn)
        {
            new DetectabilityGate(db, gate).Assess(expectedEffectAnn);
        }

        var configJson = unregistered ? WithUnregisteredMarker(spec.ConfigJson) : spec.ConfigJson;

        var strategy = new StrategyRow
        {
            StrategyId = spec.StrategyId,
            Family = spec.Family,
            ConfigJson = configJson,
            ExitPolicyJson = spec.ExitPolicyJson,
            HoldingHorizonDays = spec.HoldingHorizonDays,
            CreatedOn = createdOn,
            ParentStrategyId = spec.ParentStrategyId,
            Status = "candidate",
        };
        db.Strategies.Add(strategy);

        db.TrialsRegistry.Add(new TrialsRegistryRow
        {
            StrategyId = spec.StrategyId,
            RegisteredOn = createdOn,
            Kind = trialKind,
            RunKind = runKind,
        });

        // Link the (still-unlinked) hypothesis to the strategy it now backs.
        if (hypothesis is not null && hypothesis.StrategyId is null) hypothesis.StrategyId = spec.StrategyId;

        db.SaveChanges();
        return strategy;
    }

    private static string WithUnregisteredMarker(string configJson)
    {
        var node = (string.IsNullOrWhiteSpace(configJson) ? new JsonObject() : JsonNode.Parse(configJson) as JsonObject)
                   ?? new JsonObject();
        node[UnregisteredMarkerKey] = true;
        return node.ToJsonString();
    }
}
