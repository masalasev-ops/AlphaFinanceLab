namespace AlphaLab.Data.Entities;

/// <summary>
/// signals — the instrument registry (D91, FR-43; SCHEMA "SIGNAL LIBRARY"). Frozen rows: a parameter
/// change is a NEW registration (compiled code plus a doc change), never an UPDATE. signal_id is a
/// TEXT PK, so no AUTOINCREMENT question arises (rule 14).
/// </summary>
public sealed class SignalRow
{
    /// <summary>e.g. 'mom:L252s21'.</summary>
    public string SignalId { get; set; } = default!;
    /// <summary>Catalog family: momentum|reversal|lowvol|breakout|resmom|bab.</summary>
    public string Family { get; set; } = default!;
    /// <summary>Frozen params, serialized from <c>ISignal.Params</c>.</summary>
    public string ConfigJson { get; set; } = default!;
    /// <summary>The shipped scorer implementation the grades were computed by.</summary>
    public string CodeVersion { get; set; } = default!;
    public string RegisteredOn { get; set; } = default!;
}

/// <summary>
/// signal_ic — one row per grade (D91, FR-44). PK (signal_id, as_of, horizon_days): composite, so no
/// autoincrement question. rank_ic is REAL (a statistic, not money — D69 governs money only).
///
/// NO run_kind, deliberately (SCHEMA): a grade is a property of a signal and a date, not of a
/// forward/replay strategy run. There is exactly one market history to grade, so D93's per-run-kind
/// split has no analogue here, and the FR-45 backfill creates no replay generation (D95).
///
/// <c>n</c> is the count of names that actually contributed — the SCORABLE set (finding 294): as-of
/// membership ∩ priced, narrowed per signal by what that scorer could score. It is persisted because
/// a rank-IC read without its cross-section size is unreadable.
/// </summary>
public sealed class SignalIcRow
{
    public string SignalId { get; set; } = default!;
    /// <summary>Scoring day t (the grade is written once t+k resolves).</summary>
    public string AsOf { get; set; } = default!;
    /// <summary>k: 21|63 pre-registered (126 CLOSED, finding 290).</summary>
    public int HorizonDays { get; set; }
    /// <summary>Spearman rank correlation: scores at t vs t..t+k adjusted total returns.</summary>
    public double RankIc { get; set; }
    /// <summary>Names contributing to this grade.</summary>
    public int N { get; set; }
}
