namespace AlphaLab.Core.Config;

/// <summary>
/// Signal-Library grading configuration (CONFIG_REFERENCE "SignalLibrary"; D91, Phase 4.5). Descriptive
/// only — read by the FR-44 IC engine and the FR-46 read-model, and by nothing that judges a strategy.
///
/// NOTE WHAT IS **NOT** HERE (D108): the two trend-flag significance levels
/// (<c>SignalLibrary.TrendDecayAlpha</c> / <c>TrendGoneAlpha</c>) are **versioned config ROWS**, not
/// appsettings values, because the FR-46 read-model must resolve them through
/// <c>ConfigReadService.ResolveAsOf</c> to serve the watermark-pinned Phase-5 digest (finding 292), and
/// an appsettings value is not as-of resolvable. They are pinned once at checkpoint 4.5.2 before any
/// grade row exists, and the FR-45 backfill REFUSES to run while either is absent.
///
/// Follows the …Options convention (SectionName + mutable get/set defaults mirroring CONFIG_REFERENCE).
/// </summary>
public sealed class SignalLibraryOptions
{
    public const string SectionName = "SignalLibrary";

    /// <summary>Pre-registered grade horizons k, in trading days. 126 is CLOSED — rejected for v1
    /// (finding 290): NW lag = horizon against a 1-year window leaves ~2 effective observations.</summary>
    public IReadOnlyList<int> HorizonsDays { get; set; } = [21, 63];

    /// <summary>Rolling mean rank-IC windows, in years. Both are reported; the TREND FLAG is inferred on
    /// the 5-year window for BOTH horizons (D108).</summary>
    public IReadOnlyList<int> RollingWindowsYears { get; set; } = [1, 5];
}
