namespace AlphaLab.Evaluation.Construction;

/// <summary>
/// The knobs of one construction study. Deliberately NOT a CONFIG section: these are study parameters
/// recorded in the artefact, not lab settings, and inventing `ConstructionStudy:*` keys for a
/// report-only verb would violate the "never invent a key" rule (CONFIG_REFERENCE is the only source of
/// truth for keys). They arrive as CLI flags with the defaults below and every one is printed in the
/// report, so the artefact is self-describing.
///
/// THE HORIZON, CONFIDENCE AND POWER ARE NOT HERE. They come from the existing <c>Gate</c> section, so
/// the floor this study computes is the SAME arithmetic the admission gate applies. A study that
/// measured a floor against its own private z-values would produce a number no one could compare to
/// the band the arena actually enforces, which is the only reason to run it.
/// </summary>
public sealed record ConstructionStudyOptions
{
    /// <summary>The tail fraction taken long (and short). 0.10 = deciles.</summary>
    public double TailFraction { get; init; } = 0.10;

    /// <summary>
    /// Annual stock-borrow rates, in basis points, applied to the SHORT leg only.
    ///
    /// TWO ASSUMPTIONS, NOT A MEASUREMENT. The D43 cost model has no borrow term because the lab has
    /// never shorted, and borrow rates are specialist data this arena does not buy — so these are
    /// stated assumptions and the report labels them as such. 0 bp is the OPTIMISTIC BOUND and it is
    /// the one that can settle a negative answer outright: a long-short construction that fails to
    /// lower the floor even when borrowing is free fails for a reason no borrow data would rescue.
    /// </summary>
    public IReadOnlyList<double> BorrowBpPerYear { get; init; } = [0.0, 40.0];

    /// <summary>
    /// The book size the D43 impact term is priced at. <c>DummyRoster.DefaultStartingCash</c> — the
    /// arena's actual scale — rather than a round number, so Q/ADV is the ratio this lab would really
    /// face. At this scale impact is negligible and cost is spread-dominated; the report states the
    /// measured split rather than asserting it.
    /// </summary>
    public decimal Notional { get; init; } = 100_000m;

    /// <summary>Sessions per holding period, feeding the Newey–West lag (2×horizon, D48). 21 = the
    /// monthly rebalance this study constructs.</summary>
    public int HorizonSessions { get; init; } = 21;
}

/// <summary>
/// One borrow assumption's net effect, and the two SCALE-FREE numbers the decision actually turns on.
/// <paramref name="BorrowBpPerYear"/> is 0 for the long-only leg, where nothing is borrowed and the row
/// exists only so both legs render alike.
///
/// WHY SCALE-FREE MATTERS, and it is the correction this study forced. A long-short book is roughly 2×
/// leverage on the same cross-sectional bet: it doubles the tracking error AND the effect. Comparing the
/// raw detectability FLOOR between the two constructions therefore compares nothing — the floor rises
/// with TE, but so does the effect that must clear it. The t-statistic is
/// <c>IR·√T</c>, so detectability depends on the INFORMATION RATIO alone, and a construction that
/// doubles both terms leaves it untouched. `resmom:L252` demonstrated it exactly: TE ×3.01, effect
/// ×3.01, information ratio 0.374 → 0.373.
/// </summary>
/// <param name="InformationRatio">|net effect| / TE — scale-free, and unchanged by leverage.</param>
/// <param name="YearsToDetect">(ZSum / IR)² — the track length this construction needs to resolve its
/// own measured effect at the gate's confidence and power. THIS is comparable across constructions;
/// the floor is not. Infinite when the measured effect is zero.</param>
public sealed record NetEffect(
    double BorrowBpPerYear, double NetEffectAnn, double InformationRatio, double YearsToDetect);

/// <summary>
/// One construction's measured series and the floor it implies.
///
/// TRACKING ERROR IS NW-CORRECTED, not the naive standard deviation: it is σ_LR·√252, where σ_LR is the
/// Newey–West long-run sigma <see cref="Power.MdeCalculator"/> computes. That is the honest choice and
/// the conservative one — an autocorrelated active series has σ_LR > σ_naive, so the floor comes out
/// LARGER than a naive TE would suggest. A study arguing for a lower floor must not quietly pick the
/// estimator that flatters it.
/// </summary>
/// <param name="GrossEffectAnn">mean(active)·252, before costs.</param>
/// <param name="CostDragAnn">The annualised cost drag, EXCLUDING borrow (which varies per assumption).</param>
/// <param name="FloorAnn">ZSum·TE/√H — the smallest effect this construction could adjudicate at the
/// gate's horizon.</param>
public sealed record LegMeasurement(
    string Construction,
    int Observations,
    double SigmaLrDaily,
    int NwLag,
    double TrackingErrorAnn,
    double GrossEffectAnn,
    double CostDragAnn,
    IReadOnlyList<NetEffect> NetEffects,
    double FloorAnn);

/// <summary>One signal measured under both constructions. <paramref name="UncostedTrades"/> is
/// reported rather than swallowed: a name whose ADV window is incomplete cannot be priced by D43, and
/// how many there were is the reader's check on the cost figure.</summary>
public sealed record SignalMeasurement(
    string SignalId,
    string Family,
    int Rebalances,
    double MeanTailSize,
    double MeanScoredNames,
    int UncostedTrades,
    LegMeasurement LongOnly,
    LegMeasurement LongShort);

/// <summary>
/// What the arena measures for itself TODAY — the control the two constructions are judged against.
///
/// Read from the same <c>power_reports</c> σ_LR the admission gate's analytic floor uses, so the study
/// is not comparing its own new arithmetic against a number quoted from a document. If the control
/// floor and the long-only floor disagree wildly, that is a finding about the study, not about
/// long-short — which is precisely why the control is measured rather than cited.
/// </summary>
public sealed record ControlBaseline(
    string Source, int Samples, double SigmaLrDaily, double TrackingErrorAnn, double FloorAnn);

/// <summary>The whole study: what was measured, over what, under which assumptions.</summary>
public sealed record ConstructionStudyResult(
    string ArenaId,
    string Watermark,
    string FromSession,
    string ToSession,
    int Sessions,
    int HorizonYears,
    double ZSum,
    ConstructionStudyOptions Options,
    ControlBaseline? Control,
    IReadOnlyList<SignalMeasurement> Signals);
