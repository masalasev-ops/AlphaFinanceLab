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

    /// <summary>
    /// The documented defaults. They live here as constants rather than as property initialisers
    /// because of finding 301: the configuration binder ADDS to a pre-populated collection instead of
    /// replacing it, so a property initialised to <c>[21, 63]</c> and configured to <c>[21]</c> yields
    /// <c>[21, 63, 21]</c> — the horizon the operator tried to REMOVE survives, and one is duplicated.
    /// Leaving the properties empty and resolving the default explicitly removes that trap entirely
    /// rather than relying on binder subtleties that differ by collection type and framework version.
    /// </summary>
    public static readonly IReadOnlyList<int> DefaultHorizonsDays = [21, 63];

    /// <summary>Rolling mean rank-IC windows, in years (default 1 and 5).</summary>
    public static readonly IReadOnlyList<int> DefaultRollingWindowsYears = [1, 5];

    /// <summary>Configured grade horizons k, in trading days. EMPTY means "use
    /// <see cref="DefaultHorizonsDays"/>" — read <see cref="ResolvedHorizonsDays"/>, never this
    /// directly. 126 is CLOSED — rejected for v1 (finding 290): NW lag = horizon against a 1-year
    /// window leaves ~2 effective observations.</summary>
    public IReadOnlyList<int> HorizonsDays { get; set; } = [];

    /// <summary>Configured rolling windows, in years. EMPTY means "use
    /// <see cref="DefaultRollingWindowsYears"/>" — read <see cref="ResolvedRollingWindowsYears"/>.
    /// Both windows are reported; the TREND FLAG is inferred on the LONGEST for both horizons (D108).</summary>
    public IReadOnlyList<int> RollingWindowsYears { get; set; } = [];

    /// <summary>The horizons actually in force: what was configured, or the documented default when
    /// nothing was. This is what every consumer reads.</summary>
    public IReadOnlyList<int> ResolvedHorizonsDays =>
        HorizonsDays.Count > 0 ? HorizonsDays : DefaultHorizonsDays;

    /// <summary>The rolling windows actually in force.</summary>
    public IReadOnlyList<int> ResolvedRollingWindowsYears =>
        RollingWindowsYears.Count > 0 ? RollingWindowsYears : DefaultRollingWindowsYears;
}
