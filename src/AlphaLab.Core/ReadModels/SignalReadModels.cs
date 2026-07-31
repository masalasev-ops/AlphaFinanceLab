namespace AlphaLab.Core.ReadModels;

// The D91/FR-46 Signal-Library read-model (UX-16). DESCRIPTIVE ONLY — never a gate, monitor, sizing,
// eligibility or allocator input (§24.5). It grades the RULES, not the traders.
//
// Every honesty rail is resolved into the data here, not left to a chart: a verdict the effective
// sample cannot support ships as `insufficient` rather than a provisional "stable" (D108); the
// effective sample and both critical values travel WITH the verdict so a reader can recompute it; and
// the C-1 detection threshold rides along as reading context, because a signal can grade well and
// still describe an edge the arena could never confirm.

/// <summary>
/// One rolling window's grade for one (signal, horizon): the mean rank-IC with its Newey–West band.
///
/// <see cref="EffectiveN"/> is an INPUT to the verdict, not a footnote (D108): it sets the degrees of
/// freedom and therefore the critical value. It is rendered beside the flag for the same reason the
/// D107 power limitation prints its denominator — a thin number must not read as a thick one.
/// </summary>
/// <param name="WindowYears">The rolling window (1 or 5). The FLAG is inferred on 5y only (D108).</param>
/// <param name="MeanRankIc">Mean rank-IC over the window.</param>
/// <param name="BandLo">Newey–West lower band at the pinned significance level (null when untestable).</param>
/// <param name="BandHi">Newey–West upper band (null when untestable).</param>
/// <param name="Observations">Grade rows in the window — the NOMINAL count.</param>
/// <param name="EffectiveN">Independent observations (window ÷ horizon) — the count the test rests on.</param>
public sealed record SignalWindowGrade(
    int WindowYears, double MeanRankIc, double? BandLo, double? BandHi, int Observations, int EffectiveN);

/// <summary>
/// One registered instrument's panel row: its frozen identity, both rolling windows, and the single
/// trend verdict (inferred on 5y for both horizons — D108).
/// </summary>
/// <param name="Flag">stable | decaying | gone | insufficient — resolved here, never client-side.</param>
/// <param name="FlagReason">Why a verdict is withheld, when it is. Null when a flag was emitted.</param>
/// <param name="LevelCritical">The one-sided critical value the "gone" arm used (df = n−1).</param>
/// <param name="TrendCritical">The one-sided critical value the "decaying" arm used (df = n−2).</param>
public sealed record SignalPanelRow(
    string SignalId,
    string Family,
    int HorizonDays,
    string CodeVersion,
    IReadOnlyList<SignalWindowGrade> Windows,
    string Flag,
    string? FlagReason,
    double? TStat,
    double? LevelCritical,
    double? TrendCritical)
{
    public const string ReasonBelowEffectiveSampleFloor = "below_effective_sample_floor";
    public const string ReasonNotPinned = "thresholds_not_pinned";
}

/// <summary>
/// The FR-46 Signal-Library read-model.
///
/// <see cref="DetectionContext"/> carries the Phase-4 C-1 result verbatim as READING CONTEXT. Signal IC
/// and strategy detection are different quantities and nothing here converts between them — but a
/// reader judging whether a live-looking rule could ever be confirmed by the arena needs both numbers
/// in view at once, which is why the honest place for it is the read-model rather than a caption
/// someone might drop.
/// </summary>
public sealed record SignalLibraryReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    /// <summary>The as-of this model was resolved at — null means "current" (the live panel).</summary>
    public string? AsOf { get; init; }
    public IReadOnlyList<SignalPanelRow> Signals { get; init; } = [];
    public string? DetectionContext { get; init; }
    public static SignalLibraryReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}
