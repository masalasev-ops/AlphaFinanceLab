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
public sealed class CandidateFactory(AlphaLabDbContext db)
{
    /// <summary>The config_json property that flags an unregistered candidate (rule 16).</summary>
    public const string UnregisteredMarkerKey = "unregistered";

    /// <summary>Pre-register a hypothesis (journal_entries kind='hypothesis'), LOCKED immediately — a
    /// pre-registration is immutable except via the outcome-closure flow (D52). Returns its entry_id.</summary>
    public long RegisterHypothesis(string createdOn, string title, string bodyMd, string metric, int evidenceWindowDays, string? strategyId = null)
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
        }

        if (db.Strategies.Any(s => s.StrategyId == spec.StrategyId))
            throw new InvalidOperationException($"Strategy '{spec.StrategyId}' already exists (frozen identity, D17).");

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
