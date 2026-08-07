using AlphaLab.Core.Domain;
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
        string trialKind = "new", string runKind = "live", string status = "candidate")
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
        if (hypothesisEntryId is null && !unregistered && !IsControl(status))
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
        if (!IsControl(status) && gate is not null && hypothesis?.ExpectedEffectAnn is { } expectedEffectAnn)
        {
            var verdict = new DetectabilityGate(db, gate).Assess(expectedEffectAnn);

            // D110 as amended by D113: the floor the gate computes was previously DISCARDED, which is
            // what left the margin channel with no first link. Stamped here ONLY IF the row does not
            // already carry one — a seat-authored proposal was stamped at ASSESSMENT, and overwriting it
            // at admission would silently restore the very admission-time reading D113 replaced (and
            // would make a real proposal incomparable with its paper control, which never gets here).
            if (hypothesis.DetectabilityFloorAnn is null && verdict.Details is { } d)
            {
                hypothesis.DetectabilityFloorAnn = d.FloorAnn;
            }
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
            Status = status,
        };
        db.Strategies.Add(strategy);

        // A CONTROL REGISTERS NO TRIAL (D81 rule 4). The twin exists to PRICE the seat, not to compete:
        // it is never promotable alone, so it never spends a trial. This add was unconditional, and a
        // control passing through it would have inflated the deflated-Sharpe trials count for EVERY
        // other strategy in the arena — raising the D89 floor for candidates that had nothing to do
        // with it. The SignalRegistrar doctrine, applied here: "a registration is NOT a candidate".
        if (!IsControl(status))
        {
            db.TrialsRegistry.Add(new TrialsRegistryRow
            {
                StrategyId = spec.StrategyId,
                RegisteredOn = createdOn,
                Kind = trialKind,
                RunKind = runKind,
            });
        }

        // Link the (still-unlinked) hypothesis to the strategy it now backs.
        if (hypothesis is not null && hypothesis.StrategyId is null) hypothesis.StrategyId = spec.StrategyId;

        db.SaveChanges();
        return strategy;
    }

    /// <summary>
    /// Register the no-LLM twin (or any control) beside its treatment: a strategies row with
    /// <c>status='control'</c>, NO trials row, and no pre-registered hypothesis of its own.
    ///
    /// A control has no hypothesis because it makes no claim — it is the counterfactual the claim is
    /// measured against (D81 rule 4; the random-population precedent). It is therefore exempt from the
    /// D52 pre-registration gate rather than smuggled past it with the `unregistered` marker, which
    /// means something different: that a candidate was created sloppily, and renders permanently on the
    /// card (rule 16). A control is deliberately un-preregistered, not carelessly so.
    /// </summary>
    public StrategyRow CreateControl(CandidateSpec spec, string createdOn) =>
        CreateCandidate(spec, hypothesisEntryId: null, unregistered: false, createdOn, status: ControlStatus);

    /// <summary>The status token SCHEMA admits for a non-competing reference row.</summary>
    public const string ControlStatus = "control";

    private static bool IsControl(string status) => string.Equals(status, ControlStatus, StringComparison.Ordinal);

    // The marker now writes THROUGH the D133 shape (StrategyConfigJson), not around it. This method
    // parsed the payload as an untyped JsonNode and re-emitted it with `ToJsonString()` — no
    // AlphaLabJson.Options — so a config that arrived snake_case could leave in another convention, and
    // one column carried two. D133 also settles the two-writers-one-key problem: the typed
    // StrategyConfig.Unregistered is authoritative and this is a write through it.
    private static string WithUnregisteredMarker(string configJson) =>
        StrategyConfigJson.WithUnregisteredMarker(configJson);
}
