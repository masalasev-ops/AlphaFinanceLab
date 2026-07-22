using AlphaLab.Data;

namespace AlphaLab.Evaluation;

/// <summary>
/// The run-kind-scoped view of strategy status (Phase 4 / D37). `strategies.status` is FORWARD state:
/// a REPLAY evaluation must neither read it as its own promotion history nor mutate it — a replay
/// Promoted flipping the shared column to 'live' (or an auto-retire to 'retired') would be a replay
/// row reaching forward state, the exact leak the quarantine forbids ("replay is never a promotion
/// input", DESIGN_IMPROVEMENTS §5). Replay's promote/retire dynamics are instead DERIVED from its own
/// quarantined records: the run_kind='replay' go_live_log promotions and overfitting_status retires.
/// For run_kind='live' this resolves to `strategies.status` verbatim — forward behaviour unchanged.
/// </summary>
public static class EffectiveStatus
{
    /// <summary>Per-strategy effective status under <paramref name="runKind"/>. Retired (latest replay
    /// monitor status) wins over promoted (any replay Promoted event); base status otherwise.</summary>
    public static Dictionary<string, string> Resolve(AlphaLabDbContext db, string runKind)
    {
        var statuses = db.Strategies
            .Select(s => new { s.StrategyId, s.Status })
            .ToDictionary(s => s.StrategyId, s => s.Status, StringComparer.Ordinal);
        if (runKind == "live") return statuses;

        var promoted = db.GoLiveLog
            .Where(g => g.RunKind == runKind && g.Promoted != null)
            .Select(g => g.Promoted!)
            .Distinct()
            .ToHashSet(StringComparer.Ordinal);
        var retired = db.OverfittingStatus
            .Where(o => o.RunKind == runKind)
            .Select(o => new { o.StrategyId, o.AsOf, o.Status })
            .AsEnumerable()
            .GroupBy(o => o.StrategyId, StringComparer.Ordinal)
            .Where(g => g.OrderByDescending(o => o.AsOf, StringComparer.Ordinal).First().Status == "retired")
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in statuses.Keys.ToList())
        {
            if (retired.Contains(id)) statuses[id] = "retired";
            else if (promoted.Contains(id) && statuses[id] == "candidate") statuses[id] = "live";
        }
        return statuses;
    }
}
