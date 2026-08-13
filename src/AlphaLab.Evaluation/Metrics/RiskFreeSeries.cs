using AlphaLab.Data;

namespace AlphaLab.Evaluation.Metrics;

/// <summary>
/// The per-day risk-free rates for one window, with the coverage that produced them.
/// </summary>
/// <param name="Daily">One rate per return step, aligned positionally to the return series. Uncovered
/// steps carry 0.0 — see <see cref="FullyCovered"/> before using this for a DISPLAYED absolute figure.</param>
/// <param name="Covered">Steps for which a real RF observation existed.</param>
/// <param name="Total">Steps in the window.</param>
public readonly record struct RiskFreeWindow(IReadOnlyList<double> Daily, int Covered, int Total)
{
    /// <summary>True when every step had a real observation. **This is the field that decides whether an
    /// absolute alpha or Sharpe may be shown without the `rf_placeholder` reason.** A partially-covered
    /// window still computes — the zeros are the pre-D41 behaviour and no worse than it — but the number
    /// it produces is not one the honesty channel may present as RF-adjusted.</summary>
    public bool FullyCovered => Total > 0 && Covered == Total;

    /// <summary>An all-zero window of the requested length: no RF data at all. Named rather than
    /// constructed inline so the "we have nothing" case is greppable.</summary>
    public static RiskFreeWindow Absent(int total) => new(new double[total], 0, total);
}

/// <summary>
/// Reads the D41 RF series out of `factor_returns` and aligns it to a set of dates.
///
/// **WHY A SERIES AND NOT A SCALAR.** `MetricsConstants.RiskFreePlaceholderAnnual` was one number for all
/// time, and hard rule 6 asks for the French RF series — which moves. `PairedEffect.cs` already recorded
/// what a constant costs: RF cancels in a paired difference and shifts α only through β ≠ 1, "a known
/// bias of known sign rather than an unexamined one", deferred explicitly to Phase 6. This is Phase 6.
///
/// **ALIGNMENT IS POSITIONAL AND THE DATES COME FROM THE RETURN STEPS.** A return step is a move BETWEEN
/// two sessions, and the rate that applies to it is the one for the session it ENDS on — the same
/// convention the return itself uses (`r_t = e_t/e_{t−1} − 1` is indexed by t).
///
/// **AN UNCOVERED DAY IS NOT SILENTLY ZERO — it is zero AND counted.** Rule 10 forbids the silent default,
/// not the arithmetic: the caller gets both the series and the coverage, and the honesty channel refuses
/// to present an absolute figure whose window was not fully covered. That refusal is what
/// `MetricCell.ReasonRfPlaceholder` was declared for in Phase 3 and never wired to.
/// </summary>
public sealed class RiskFreeSeries
{
    /// <summary>The SCHEMA token for the risk-free column (`factor_returns.factor`).</summary>
    public const string RfFactor = "RF";

    private readonly Dictionary<string, double> _byDate;

    private RiskFreeSeries(Dictionary<string, double> byDate) => _byDate = byDate;

    /// <summary>Loads the whole RF series once. Callers hold it for a run rather than querying per
    /// strategy: the monitor evaluates every strategy against the same dates, so a per-strategy read
    /// would be the same rows N times.</summary>
    public static RiskFreeSeries Load(AlphaLabDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var map = db.FactorReturns
            .Where(f => f.Factor == RfFactor)
            .Select(f => new { f.Date, f.Value })
            .AsEnumerable()
            .ToDictionary(f => f.Date, f => f.Value, StringComparer.Ordinal);
        return new RiskFreeSeries(map);
    }

    /// <summary>An empty series — no RF data. The pre-D41 behaviour, reachable whenever the refresh has
    /// never run, and the state every arena is in until it does.</summary>
    public static RiskFreeSeries Empty() => new(new Dictionary<string, double>(StringComparer.Ordinal));

    public int Count => _byDate.Count;

    /// <summary>The rates for <paramref name="dates"/>, positionally aligned, with coverage.</summary>
    public RiskFreeWindow For(IReadOnlyList<string> dates)
    {
        ArgumentNullException.ThrowIfNull(dates);
        var daily = new double[dates.Count];
        var covered = 0;
        for (var i = 0; i < dates.Count; i++)
        {
            if (_byDate.TryGetValue(dates[i], out var v)) { daily[i] = v; covered++; }
        }
        return new RiskFreeWindow(daily, covered, dates.Count);
    }

    /// <summary>
    /// Subtracts a per-day risk-free rate from a return series, positionally.
    ///
    /// **SUBTRACTING AT THE SOURCE IS WHY NO METRIC SIGNATURE CHANGED.** Every statistic RF touches wants
    /// the same thing — Jensen's α fits (r_s − r_f) on (r_b − r_f), Sharpe is mean/sd of (r − r_f) — so
    /// converting once, where the aligned series is built, leaves `JensenAlpha(…, 0.0, lag)` and
    /// `Sharpe(…, 0.0)` EXACTLY CORRECT on the excess inputs. The alternative (a per-day overload on each
    /// metric) threads the same series to more places and gives two ways to spell one idea, which is how
    /// the harness and production drift apart — the defect D138 had to fix once already.
    ///
    /// **AND IT LEAVES THE PAIRED DIFFERENCE PROVABLY UNTOUCHED**, which is the property the gate depends
    /// on: d_t = (r_s − rf_t) − (r_b − rf_t) = r_s − r_b, identically, for ANY rf. So the D31 paired test,
    /// the D48 MDE and the promotion gate's head-to-head cannot move no matter what RF does — not as an
    /// argument, but as cancellation in the expression itself.
    /// </summary>
    public static List<double> Excess(IReadOnlyList<double> returns, RiskFreeWindow rf)
    {
        ArgumentNullException.ThrowIfNull(returns);
        if (rf.Daily.Count != returns.Count)
        {
            throw new ArgumentException(
                $"The risk-free window has {rf.Daily.Count} observations but the return series has " +
                $"{returns.Count}. Positional alignment is the contract — a silent truncation would shift " +
                "every rate by the offset and produce a plausible, wrong number.", nameof(rf));
        }

        var excess = new List<double>(returns.Count);
        for (var i = 0; i < returns.Count; i++) excess.Add(returns[i] - rf.Daily[i]);
        return excess;
    }
}
