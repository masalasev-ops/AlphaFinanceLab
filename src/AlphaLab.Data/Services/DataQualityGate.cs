using System.Globalization;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>How a quality flag should be acted on. <see cref="Reject"/> = fail closed: the bar cannot
/// be trusted or priced (NaN / non-positive price), so the write is blocked with a logged reason (rule
/// 10) — the actual block happens in the Phase-2 staged pipeline (D53), which consumes this report.
/// <see cref="Warn"/> = flagged/alarmed for investigation (a gap, a return outlier, a reconciliation
/// alarm, a cross-check mismatch) but does NOT drop otherwise-valid price bars.</summary>
public enum QualitySeverity
{
    Warn,
    Reject
}

/// <summary>The FR-6 quality-gate issue kinds. The first four are computed by <see cref="DataQualityGate"/>;
/// <see cref="CrossCheckMismatch"/> is raised by the (dormant at launch) Alpaca cross-check seam.</summary>
public enum QualityIssue
{
    /// <summary>An expected trading session has no bar (a gap). Expected sessions are supplied by the
    /// caller; the calendar that produces them is FR-30 (checkpoint 1.8).</summary>
    MissingBar,

    /// <summary>A required OHLC field is null, NaN, or infinite (e.g. a NaN close). Fail closed.</summary>
    NanField,

    /// <summary>A price field is finite but ≤ 0 — a NaN-adjacent corruption. Fail closed.</summary>
    NonPositivePrice,

    /// <summary>A daily (adjusted) return whose robust z-score exceeds <c>Data.OutlierZ</c>.</summary>
    OutlierReturn,

    /// <summary>The adj_close/close factor stepped between two sessions with no dividend/split in the
    /// event feed to explain it — the FR-6 reconciliation alarm (a corporate action is missing).</summary>
    UnexplainedAdjustment,

    /// <summary>A rotating-sample close disagrees with the Alpaca cross-check beyond tolerance. Raised
    /// only once the Alpaca seam is activated (dormant at launch — no Alpaca account).</summary>
    CrossCheckMismatch
}

/// <summary>One data-quality finding for a security's bar series (FR-6). Pure data — carries no
/// side effect; the pipeline decides what to do with it by <see cref="Severity"/>.</summary>
public sealed record QualityFlag(
    QualityIssue Issue,
    QualitySeverity Severity,
    string Symbol,
    string? Date,
    string Detail);

/// <summary>The result of running the quality gate over one security's candidate bars (FR-6).</summary>
public sealed record QualityReport(IReadOnlyList<QualityFlag> Flags)
{
    public static readonly QualityReport Empty = new([]);

    /// <summary>No flags at all — the series is clean.</summary>
    public bool IsClean => Flags.Count == 0;

    /// <summary>Any fail-closed flag present ⇒ the pipeline blocks the write (rule 10).</summary>
    public bool HasRejects => Flags.Any(f => f.Severity == QualitySeverity.Reject);
}

/// <summary>
/// Quality-gate configuration (CONFIG_REFERENCE "Data"). <see cref="OutlierZ"/> drives the return
/// outlier test; <see cref="BarCrossCheckSampleSize"/> / <see cref="BarCrossCheckTolerancePct"/> are
/// wired for the Alpaca cross-check but INERT at launch (the seam is dormant — no Alpaca account).
/// Follows the …Options convention (SectionName + mutable get/set defaults matching CONFIG).
/// </summary>
public sealed class DataQualityOptions
{
    public const string SectionName = "Data";

    /// <summary>Robust-z cutoff for the daily-return outlier test (CONFIG default 8.0).</summary>
    public double OutlierZ { get; set; } = 8.0;

    /// <summary>Rotating names/day cross-checked vs Alpaca (CONFIG default 10). INERT at launch.</summary>
    public int BarCrossCheckSampleSize { get; set; } = 10;

    /// <summary>Alpaca vs EODHD close tolerance, percent (CONFIG default 0.5). INERT at launch.</summary>
    public double BarCrossCheckTolerancePct { get; set; } = 0.5;
}

/// <summary>
/// FR-6 data-quality gate. Given a security's candidate bars (the in-memory fetch, BEFORE any write),
/// the corporate-action feed, and the set of expected trading sessions, it returns a
/// <see cref="QualityReport"/> of flags: gaps, NaN/non-positive fields, robust-z return outliers, and
/// the dividend/split reconciliation alarm (an adj_close/close factor step with no matching event).
/// It is PURE — no DB reads, no writes, no side effects — so it is unit-tested offline and the Phase-2
/// staged pipeline (D53) consumes the report to fail closed on rejects or alarm on warnings. Persisting
/// flags for the Data-health screen is a read-model concern (Phase 3/7), not this gate's job.
/// </summary>
public interface IDataQualityGate
{
    /// <summary>Evaluate one security's candidate bar series. <paramref name="expectedDates"/> is the
    /// set of sessions the series SHOULD cover (from the calendar, FR-30/1.8); pass null to skip the
    /// gap check when no calendar context is available. <paramref name="actions"/> is the security's
    /// dividend/split feed used to explain adj_close/close factor steps.</summary>
    QualityReport Evaluate(
        string symbol,
        IReadOnlyList<EodBar> bars,
        IReadOnlyList<CorporateActionRow> actions,
        IReadOnlyCollection<string>? expectedDates = null);
}

public sealed class DataQualityGate(DataQualityOptions options) : IDataQualityGate
{
    /// <summary>The adj_close/close factor is piecewise-constant absent an event; this relative
    /// epsilon absorbs the rounding noise in a provider's 4-dp adjusted_close so only a REAL step
    /// (a dividend/split) trips the reconciliation. Well below a real quarterly dividend yield.
    /// Stop-and-report seam: promote to a Data.* config key if a live backfill shows factor noise
    /// approaching it.</summary>
    private const double AdjustmentRatioEpsilon = 0.0005;

    /// <summary>Minimum returns needed before the robust-z outlier test is meaningful; below this the
    /// scale (MAD) is undefined, so the test is skipped rather than fabricating a verdict.</summary>
    private const int MinReturnsForOutlier = 4;

    /// <summary>0.6745 = Φ⁻¹(0.75): divides the median-absolute-deviation into a consistent estimate of
    /// σ for normal data, so a robust z compares like-for-like against <c>Data.OutlierZ</c>.</summary>
    private const double MadToSigma = 0.6745;

    /// <summary>Floor on the robust return-dispersion estimate (0.1%/day). A near-constant or halted/
    /// illiquid series has a MAD at (or numerically near) zero; without a floor that either manufactures
    /// outliers from ordinary moves OR — worse — masks a real bad print in a mostly-flat series (a lone
    /// 40% spike among flat days has a zero MAD too). Flooring the scale fixes both: dispersion can never
    /// read below 0.1%/day, so a genuine error still trips the cutoff and an ordinary move never does.
    /// Stop-and-report seam: promote to a Data.* config key if a live series' true dispersion nears it.</summary>
    private const double MinReturnDispersion = 0.001;

    public QualityReport Evaluate(
        string symbol,
        IReadOnlyList<EodBar> bars,
        IReadOnlyList<CorporateActionRow> actions,
        IReadOnlyCollection<string>? expectedDates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(actions);

        var flags = new List<QualityFlag>();

        // Sort by trading date (ISO-8601 sorts chronologically as an ordinal string compare), so the
        // return series, factor steps, and gap detection all see the sessions in order regardless of
        // the order the provider delivered them.
        var ordered = bars.OrderBy(b => b.Date, StringComparer.Ordinal).ToList();

        CheckGaps(symbol, ordered, expectedDates, flags);
        var priced = CheckFieldIntegrity(symbol, ordered, flags);   // returns the bars with a usable price
        CheckReturnOutliers(symbol, priced, flags);
        CheckAdjustmentReconciliation(symbol, priced, actions, flags);

        return new QualityReport(flags);
    }

    // ---- Gaps: an expected session with no delivered bar ----
    private static void CheckGaps(
        string symbol,
        IReadOnlyList<EodBar> ordered,
        IReadOnlyCollection<string>? expectedDates,
        List<QualityFlag> flags)
    {
        if (expectedDates is null || expectedDates.Count == 0) return;

        var present = ordered.Select(b => b.Date).ToHashSet(StringComparer.Ordinal);
        foreach (var date in expectedDates.OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!present.Contains(date))
            {
                flags.Add(new QualityFlag(QualityIssue.MissingBar, QualitySeverity.Warn, symbol, date,
                    "Expected trading session has no bar (gap)."));
            }
        }
    }

    // ---- Field integrity: NaN/inf/null (fail closed) and non-positive prices (fail closed) ----
    // Emits at most one integrity flag per bar (close first) and returns the bars that carry a usable
    // price for the downstream return/factor computations.
    private static List<(EodBar Bar, double RawClose, double? Adj, double? Factor)> CheckFieldIntegrity(
        string symbol,
        IReadOnlyList<EodBar> ordered,
        List<QualityFlag> flags)
    {
        var priced = new List<(EodBar, double, double?, double?)>(ordered.Count);
        foreach (var b in ordered)
        {
            if (!b.Close.HasValue || !double.IsFinite(b.Close.Value))
            {
                flags.Add(new QualityFlag(QualityIssue.NanField, QualitySeverity.Reject, symbol, b.Date,
                    "Close is null, NaN, or infinite."));
                continue;
            }
            if (b.Close.Value <= 0)
            {
                flags.Add(new QualityFlag(QualityIssue.NonPositivePrice, QualitySeverity.Reject, symbol, b.Date,
                    $"Close is not positive ({b.Close.Value.ToString(CultureInfo.InvariantCulture)})."));
                continue;
            }

            // Close is good; the open/high/low, when supplied, must also be finite and positive.
            var badOhl = BadOhl(b);
            if (badOhl is not null)
            {
                flags.Add(new QualityFlag(QualityIssue.NanField, QualitySeverity.Reject, symbol, b.Date, badOhl));
                continue;
            }

            // A PRESENT adjusted close must itself be finite and positive — it is the field every
            // downstream return/alpha prices on, so a corrupt one fails closed (rule 10) rather than
            // being silently defaulted to the raw close. A genuinely ABSENT (null) adjusted close is
            // benign: the raw close is a valid bar; it simply carries no adjusted price / factor.
            if (b.AdjClose is { } adj)
            {
                if (!double.IsFinite(adj))
                {
                    flags.Add(new QualityFlag(QualityIssue.NanField, QualitySeverity.Reject, symbol, b.Date,
                        "Adjusted close is NaN or infinite."));
                    continue;
                }
                if (adj <= 0)
                {
                    flags.Add(new QualityFlag(QualityIssue.NonPositivePrice, QualitySeverity.Reject, symbol, b.Date,
                        $"Adjusted close is not positive ({adj.ToString(CultureInfo.InvariantCulture)})."));
                    continue;
                }
            }

            var factor = b.AdjClose is { } a ? a / b.Close.Value : (double?)null; // a is validated (>0, finite)
            priced.Add((b, b.Close.Value, b.AdjClose, factor));
        }
        return priced;
    }

    private static string? BadOhl(EodBar b)
    {
        foreach (var (name, v) in new[] { ("open", b.Open), ("high", b.High), ("low", b.Low) })
        {
            if (v.HasValue && (!double.IsFinite(v.Value) || v.Value <= 0))
            {
                return $"{name} is NaN/infinite or not positive.";
            }
        }
        return null;
    }

    // ---- Return outliers: robust (median/MAD) z beyond Data.OutlierZ ----
    // A plain sample z self-masks — a single huge print inflates the std enough to pull its own z below
    // the cutoff (a 12σ spike among ~20 normal days scores ~4 on a sample z). The median/MAD z is
    // immune to that, so the FR-6 12σ outlier is actually caught.
    private void CheckReturnOutliers(
        string symbol,
        IReadOnlyList<(EodBar Bar, double RawClose, double? Adj, double? Factor)> priced,
        List<QualityFlag> flags)
    {
        // One CONSISTENT price basis for the whole series: the adjusted close when the series carries
        // adjusted closes (so genuine split/dividend steps never read as outliers), otherwise the raw
        // close (e.g. an index with no adjustment). Never build a return across a raw/adjusted boundary
        // — a lone missing adjusted close would manufacture a spurious jump — so a bar with no basis
        // price (missing adj in an adjusted series) is simply skipped as a return endpoint.
        var anyAdj = priced.Any(p => p.Adj is not null);

        var returns = new List<(string Date, double Ret)>(priced.Count);
        (double Price, string Date)? prev = null;
        foreach (var p in priced)
        {
            double? basis = anyAdj ? p.Adj : p.RawClose;
            if (basis is not { } price) continue; // no basis price -> not a valid return endpoint
            if (prev is { } pr) returns.Add((p.Bar.Date, price / pr.Price - 1.0));
            prev = (price, p.Bar.Date);
        }

        if (returns.Count < MinReturnsForOutlier) return;

        var vals = returns.Select(r => r.Ret).ToArray();
        var median = Median(vals);
        var mad = Median(vals.Select(r => Math.Abs(r - median)).ToArray());
        // Consistent σ estimate from the MAD, floored so a degenerate (near-constant/illiquid) series
        // can neither manufacture nor mask an outlier. Never skip on mad==0 — that would miss a lone
        // bad print in an otherwise-flat series (its MAD is zero too).
        var scale = Math.Max(mad / MadToSigma, MinReturnDispersion);

        foreach (var (date, ret) in returns)
        {
            var robustZ = Math.Abs(ret - median) / scale;
            if (robustZ > options.OutlierZ)
            {
                flags.Add(new QualityFlag(QualityIssue.OutlierReturn, QualitySeverity.Warn, symbol, date,
                    $"Daily return {ret.ToString("P2", CultureInfo.InvariantCulture)} has robust z " +
                    $"{robustZ.ToString("F1", CultureInfo.InvariantCulture)} > {options.OutlierZ.ToString(CultureInfo.InvariantCulture)}."));
            }
        }
    }

    // ---- Reconciliation: an adj_close/close factor step with no dividend/split to explain it ----
    // The factor is piecewise-constant absent a corporate action; a step between two sessions must be
    // covered by a dividend ex-date or split effective date in (prev.date, cur.date]. A step with no
    // such event is the FR-6 reconciliation alarm — a corporate action is missing from the feed.
    // PRESENCE-BASED by design: a step is "explained" by the mere presence of ANY dividend/split in the
    // window, not by matching the step MAGNITUDE to the event's amount/ratio. This is deliberately
    // conservative (zero false positives; it only alarms when there is NO event at all) — a magnitude-
    // aware residual check needs the exact adj_close factor arithmetic (dividend factor = f(div, close),
    // split factor = ratio), which has no byte-real fixture to validate against yet, so building it now
    // would risk false positives on real data. Known limitation (recorded in PROGRESS, stop-and-report
    // seam for the live backfill): a missing action co-located in the same window as a present, unrelated
    // event (e.g. a missing split sharing an ex-date with a present dividend) is not flagged.
    private static void CheckAdjustmentReconciliation(
        string symbol,
        IReadOnlyList<(EodBar Bar, double RawClose, double? Adj, double? Factor)> priced,
        IReadOnlyList<CorporateActionRow> actions,
        List<QualityFlag> flags)
    {
        var adjustmentDates = actions
            .Where(a => a.Type is "dividend" or "split")
            .Select(a => a.ExDate ?? a.EffectiveDate)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();

        (EodBar Bar, double Factor)? prev = null;
        foreach (var p in priced)
        {
            if (p.Factor is not { } f || !double.IsFinite(f) || f <= 0) continue; // no computable factor
            if (prev is { } pr)
            {
                var ratio = f / pr.Factor;
                if (Math.Abs(ratio - 1.0) > AdjustmentRatioEpsilon)
                {
                    var explained = adjustmentDates.Any(d =>
                        string.CompareOrdinal(pr.Bar.Date, d) < 0 && string.CompareOrdinal(d, p.Bar.Date) <= 0);
                    if (!explained)
                    {
                        flags.Add(new QualityFlag(QualityIssue.UnexplainedAdjustment, QualitySeverity.Warn, symbol,
                            p.Bar.Date,
                            $"adj/raw factor stepped {(ratio - 1.0).ToString("P2", CultureInfo.InvariantCulture)} " +
                            $"from {pr.Bar.Date} with no dividend/split in the feed (missing corporate action)."));
                    }
                }
            }
            prev = (p.Bar, f);
        }
    }

    /// <summary>Median of a non-empty array (sorted copy; average of the two middles for even n).</summary>
    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
