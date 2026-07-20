namespace AlphaLab.Core.ReadModels;

// The D58 honesty read-model DTOs. The UX rules (UX-1..UX-6, UX-12, UX-15) are resolved into these
// FIELDS by the AlphaLab.Evaluation builders and rendered verbatim by any client — a UI that recomputes
// whether to dim a cell, which tier a row is in, or whether a chip shows is a bug. Serialized snake_case
// via AlphaLabJson; a forward read-model can never carry a replay row (FR-33) by construction of its builder.

/// <summary>The MDE band attached to a metric cell (UX-2/UX-6): the smallest gap the current track can judge.</summary>
public sealed record MetricMde(double Estimate);

/// <summary>
/// UX-1/UX-6: a numeric cell that carries its OWN honesty. <see cref="Display"/> is authoritative — a cell
/// that computes whether to dim is a bug. When a head-to-head gap sits inside the MDE the builder emits
/// display="dimmed", prefix="~", reason="inside_mde"; an absolute alpha/Sharpe under the RF placeholder
/// carries reason="rf_placeholder".
/// </summary>
public sealed record MetricCell(double? Value, string Formatted, string Display, string Prefix, string? Reason, MetricMde? Mde)
{
    public const string DisplayNormal = "normal";
    public const string DisplayDimmed = "dimmed";
    public const string ReasonInsideMde = "inside_mde";
    public const string ReasonRfPlaceholder = "rf_placeholder";

    public static readonly MetricCell None = new(null, "—", DisplayNormal, "", null, null);

    public static MetricCell Normal(double value, string formatted, MetricMde? mde = null, string? reason = null) =>
        new(value, formatted, DisplayNormal, "", reason, mde);

    /// <summary>UX-1: a gap inside the MDE renders at reduced contrast with a tilde prefix.</summary>
    public static MetricCell DimmedInsideMde(double value, string formatted, MetricMde mde) =>
        new(value, formatted, DisplayDimmed, "~", ReasonInsideMde, mde);
}

/// <summary>UX-4c: "97th pct of 200 matched randoms" — the S3 rank, rendered verbatim.</summary>
public sealed record PopulationPercentile(double Pct, int N);

/// <summary>UX-12/D63: the separation state beside (never instead of) the verdict. <see cref="Days"/> is
/// the track length; the IndistinguishableFromRandom chip renders when State=="none" past MinTrackDays.</summary>
public sealed record SeparationInfo(string State, int Days, int MinTrackDays)
{
    public const string None = "none";
    public const string Emerging = "emerging";
    public const string Distinguishable = "distinguishable";

    /// <summary>True when the IndistinguishableFromRandom chip should render (UX-12).</summary>
    public bool IsIndistinguishable => State == None && Days >= MinTrackDays;
}

/// <summary>
/// UX-1 / §23.6: one strategy leaderboard row. The verdict chip is the highest-contrast element; α dims
/// inside the MDE; rows group into <see cref="Tier"/>s with no ordinal rank inside a tier. <see cref="Seat"/>
/// ('math'|'ai') badges the roster — the honest arena is LLM-free, so Phase 3 is always 'math'.
/// </summary>
public sealed record StrategyRow(
    string Id,
    string Name,
    bool IsLive,
    string Seat,
    string VerdictChip,
    string Tier,
    MetricCell Alpha,
    PopulationPercentile? PopulationPercentile,
    SeparationInfo? Separation,
    bool TurnoverCaveat)
{
    public const string SeatMath = "math";
    public const string SeatAi = "ai";

    public const string TierDistinguishableAbove = "distinguishable-above";
    public const string TierNotYetDistinguishable = "not-yet-distinguishable";
    public const string TierBelowOrFlagged = "below-or-flagged";
    public const string TierReference = "reference";
}

/// <summary>UX-9/D51: one allocation derivation row — α̂ ± se → α̃ → target → applied → weight, with the
/// clamp(s) that bound it. Reconstructible from allocation_log.</summary>
public sealed record AllocationRowDto(
    string Strategy, double AlphaHatPct, double SePct, double AlphaTildePct,
    double Target, double Applied, double Weight, IReadOnlyList<string> ClampsBound);

/// <summary>UX-4a/b: the population's 5–95% band under an equity/alpha chart (one shaded area, never
/// per-member rows). <see cref="Role"/> distinguishes the cost-on null from the display-only cost-free band.</summary>
public sealed record PopulationBand(double P5, double P50, double P95, int N, string Role);
