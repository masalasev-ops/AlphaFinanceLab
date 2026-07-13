namespace AlphaLab.Core.ReadModels;

// Empty, honest Phase-0 read-models — one per §15 screen the API projects (D57/D58).
// Each carries a ReadModelStamp and an empty Rows collection; before the first committed
// run every stamp is ReadModelStamp.NoRunYet. Row *shapes* (StrategyRow, AllocationRow, …
// with their MDE/verdict/percentile/clamp fields) are resolved in AlphaLab.Evaluation in
// later phases — Phase 0 deliberately ships them empty so the client renders empty-state.
//
// Rows are typed as IReadOnlyList<object> on purpose: committing to the detailed D58 row
// shapes here would pre-empt the evaluation read-model builders. They stay empty in Phase 0.

public sealed record StrategiesReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static StrategiesReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record StrategyDetailReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public object? Strategy { get; init; }
    public static StrategyDetailReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record LiveReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static LiveReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record AllocationReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static AllocationReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record GoLiveLogReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static GoLiveLogReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record TradesReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static TradesReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record OverfittingHealthReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Signals { get; init; } = [];
    public static OverfittingHealthReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record RegimesReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static RegimesReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record RiskReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static RiskReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record DataHealthReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    /// <summary>D72: surfaced when a run_in_progress flag is stale. False before any run.</summary>
    public bool StaleRunDetected { get; init; }
    public static DataHealthReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record JournalReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static JournalReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

/// <summary>GET /api/v1/why-trade/{strategyId}/{date} — single-object "why did this strategy trade" detail.</summary>
public sealed record WhyTradeReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public object? Detail { get; init; }
    public static WhyTradeReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

/// <summary>GET /api/v1/admin/interventions — D55 admin-action audit read side.</summary>
public sealed record AdminInterventionsReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static AdminInterventionsReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

public sealed record ActivityReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static ActivityReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}

/// <summary>
/// Replay artifacts are always quarantined (rule 1 / D58): served only from /api/v1/replay,
/// flagged quarantined:true, and never present in any forward read-model by construction.
/// </summary>
public sealed record ReplayReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public bool Quarantined { get; init; } = true;
    public IReadOnlyList<object> Rows { get; init; } = [];
    public static ReplayReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}
