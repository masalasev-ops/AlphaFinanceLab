namespace AlphaLab.Core.ReadModels;

/// <summary>
/// One factor's loading in the D41 attribution regression, with the honesty a bare β would not carry.
/// </summary>
/// <param name="Factor">The SCHEMA token — `MKT_RF`, `SMB`, `HML`, `UMD`, `RMW`.</param>
/// <param name="Beta">The loading.</param>
/// <param name="StdError">Its Newey–West (HAC) standard error.</param>
/// <param name="TStat">β / SE, or 0 when the SE is degenerate — the `OlsFit.AlphaT` convention.</param>
/// <param name="Formatted">Rendered verbatim by the client (rule 18): the builder decides the precision,
/// not the UI.</param>
public sealed record FactorLoading(string Factor, double Beta, double StdError, double TStat, string Formatted);

/// <summary>
/// The D41 factor-attribution panel — *"what is this strategy, really?"*, answered as
/// `r_s − r_f = α + β_mkt(Mkt−RF) + β_smb·SMB + β_hml·HML + β_umd·UMD + β_rmw·RMW + ε`
/// (DESIGN_IMPROVEMENTS §1.4), fitted with Newey–West errors.
///
/// **DIAGNOSTIC-ONLY, AND THE TYPE SAYS SO RATHER THAN A COMMENT ELSEWHERE (D41).** Nothing here is a
/// funnel input, a gate input or a promotion input. That is exactly what makes the library's publication
/// lag acceptable instead of a hidden defect — and it is why <see cref="FactorDataThrough"/> is a
/// REQUIRED field: the panel states the lag on its face, so a reader never has to assume the decomposition
/// is current.
///
/// **THE LAG NOTE IS D41's LITERAL WORDING.** The register says the panel states *"factor data through
/// &lt;date&gt;"*. <see cref="FactorDataThrough"/> carries the date and <see cref="LagNote"/> carries the
/// rendered sentence, so a client cannot invent its own phrasing or its own freshness rule.
///
/// **THE MOCKUP IS WRONG ABOUT THE LAG AND MUST NOT BE COPIED (finding 447).**
/// `docs/alphalab_ux_mockups.html` shows `'2-day lag · expected'` and `'current · 2d lag ok'` for this
/// feed. Every source of truth says WEEKS — INTEGRATIONS §3 ("the publication lag of weeks is fine"), D83
/// ("weeks of publication lag, so the most recent weeks of a formation window routinely have no factor
/// data") and DESIGN_IMPROVEMENTS §1.4 ("the library's publication lag (weeks…)"). A freshness rule built
/// from the mockup would sit permanently amber under the real cadence. This read-model therefore publishes
/// the THROUGH-DATE and no freshness verdict at all: the honest statement is "here is how current the data
/// is", not "here is whether that is OK", because the latter needs a threshold no document states.
/// </summary>
public sealed record AttributionReadModel
{
    public required ReadModelStamp Stamp { get; init; }

    /// <summary>The strategy this decomposition is for; null iff there is nothing to show.</summary>
    public string? StrategyId { get; init; }

    /// <summary>Whether a fit was produced at all. False carries <see cref="Unavailable"/>.</summary>
    public bool HasFit { get; init; }

    /// <summary>Why there is no fit — `insufficient_track`, `no_factor_data`, `factor_data_gap`,
    /// `degenerate_design`. Null iff <see cref="HasFit"/>. A named reason rather than an empty panel:
    /// "not enough track yet" and "the factor feed is stale" are different operator problems.</summary>
    public string? Unavailable { get; init; }

    /// <summary>Annualized α — what the factors do NOT explain. **Not a verdict**: D41 is diagnostic-only,
    /// so this never feeds the gate and is never compared to an MDE here.</summary>
    public double? AlphaAnnualized { get; init; }

    public string? AlphaFormatted { get; init; }

    /// <summary>α's Newey–West t-stat.</summary>
    public double? AlphaTStat { get; init; }

    /// <summary>The loadings, in the §1.4 order: Mkt−RF, SMB, HML, UMD, RMW.</summary>
    public IReadOnlyList<FactorLoading> Loadings { get; init; } = [];

    /// <summary>Return steps in the fit.</summary>
    public int N { get; init; }

    /// <summary>The Bartlett bandwidth actually used, after the `min(lag, n−1)` truncation.</summary>
    public int Lag { get; init; }

    /// <summary>The last date the factor series covers — D41's `&lt;date&gt;`.</summary>
    public string? FactorDataThrough { get; init; }

    /// <summary>The rendered lag sentence, D41's literal wording. Rendered verbatim (rule 18).</summary>
    public string? LagNote { get; init; }

    /// <summary>Sessions in the fitted window for which a factor observation existed, and the total.
    /// Published because a decomposition over a partly-covered window is a different claim from one over a
    /// full window, and the difference is not visible in α.</summary>
    public int CoveredSessions { get; init; }

    public int TotalSessions { get; init; }

    public static AttributionReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };

    public const string UnavailableInsufficientTrack = "insufficient_track";
    public const string UnavailableNoFactorData = "no_factor_data";
    public const string UnavailableFactorDataGap = "factor_data_gap";
    public const string UnavailableDegenerateDesign = "degenerate_design";
}
