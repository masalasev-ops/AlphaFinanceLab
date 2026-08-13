using AlphaLab.Data;

namespace AlphaLab.Evaluation;

/// <summary>
/// Shared equity-curve reads + point-in-time paired-return alignment used by BOTH the gate
/// (EvaluationStep) and the monitor (OverfittingMonitor). Keeping the return convention — the common-date
/// intersection and the prev≤0 skip — in one place means the MDE/gate and the S3/S6 percentile can never
/// be computed from divergent return series for the same strategy on the same day.
/// </summary>
internal static class CurveMath
{
    /// <summary>An account's forward equity curve for a run_kind, ordered by as_of.</summary>
    public static List<(string AsOf, decimal Equity)> Curve(AlphaLabDbContext db, long accountId, string runKind) =>
        db.EquityCurve
            .Where(e => e.AccountId == accountId && e.RunKind == runKind)
            .OrderBy(e => e.AsOf)
            .Select(e => new { e.AsOf, e.Equity })
            .AsEnumerable()
            .Select(e => (e.AsOf, e.Equity))
            .ToList();

    /// <summary>Daily returns between consecutive dates common to both curves (rf cancels in the paired
    /// difference, so it is omitted). A step whose prior equity is ≤ 0 is skipped (undefined return).</summary>
    public static (List<double> Strat, List<double> Bench) AlignedReturns(
        IReadOnlyList<(string AsOf, decimal Equity)> strat, IReadOnlyList<(string AsOf, decimal Equity)> bench)
    {
        var (s, b, _) = AlignedReturnsDated(strat, bench);
        return (s, b);
    }

    /// <summary>
    /// The same alignment, keeping the DATE each return step ends on (D41, checkpoint 6.6).
    ///
    /// The dated form exists because a per-day risk-free rate has to be aligned to the same steps, and
    /// the un-dated overload drops exactly the information needed to do that. A caller that re-derived
    /// the dates itself would re-implement the common-date intersection AND the `prev ≤ 0` skip — the
    /// two rules this class exists to keep in one place — and would drift the moment either changed.
    ///
    /// The step is indexed by the session it ENDS on, matching the return's own convention
    /// (`r_t = e_t/e_{t−1} − 1` is indexed by t), so `Dates[i]` is the date whose RF applies to
    /// `Strat[i]`/`Bench[i]`.
    /// </summary>
    public static (List<double> Strat, List<double> Bench, List<string> Dates) AlignedReturnsDated(
        IReadOnlyList<(string AsOf, decimal Equity)> strat, IReadOnlyList<(string AsOf, decimal Equity)> bench)
    {
        var benchByDate = new Dictionary<string, decimal>(bench.Count);
        foreach (var (asOf, equity) in bench) benchByDate[asOf] = equity;

        var common = strat.Where(s => benchByDate.ContainsKey(s.AsOf)).ToList();
        var stratReturns = new List<double>(Math.Max(0, common.Count - 1));
        var benchReturns = new List<double>(Math.Max(0, common.Count - 1));
        var dates = new List<string>(Math.Max(0, common.Count - 1));
        for (var i = 1; i < common.Count; i++)
        {
            var sPrev = common[i - 1].Equity;
            var bPrev = benchByDate[common[i - 1].AsOf];
            if (sPrev <= 0m || bPrev <= 0m) continue;
            stratReturns.Add((double)(common[i].Equity / sPrev) - 1.0);
            benchReturns.Add((double)(benchByDate[common[i].AsOf] / bPrev) - 1.0);
            dates.Add(common[i].AsOf);
        }
        return (stratReturns, benchReturns, dates);
    }
}
