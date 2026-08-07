using AlphaLab.Data;
using AlphaLab.Evaluation.Monitor;
using AlphaLab.Evaluation.Numerics;
using AlphaLab.Evaluation.Populations;

namespace AlphaLab.Evaluation.Recompute;

/// <summary>
/// The `derived-band` tier's inputs (MASTER §25.2; D117 clause 4) — the ones that are NOT stored columns:
/// the matched population's member window alphas, and each subject's own 63-day window alpha and t-stat.
///
/// **Why this tier has to exist for finding 280 to be scorable at all.** `MonitorSignals.S6` returns EARLY
/// on `rollingAlphaT &lt; S6NegativeAlphaT` and never evaluates band membership, so a row that took the
/// negative-alpha branch recorded NO band information. Move that threshold — the remaining finding-280
/// candidate — and exactly those rows fall through to a band check whose input was never stored (finding
/// 340). Recovering `insideCentralBand` from the contribution token is valid only while the threshold is
/// unchanged, which is the one case this tier is not needed for.
///
/// **Point-in-time by construction.** Every series is aligned against the benchmark ONCE and indexed by
/// as-of; a session takes the last <see cref="OverfittingMonitor.RollingWindowDays"/> returns at or before
/// its own date. Never the full series — that would hand an early session the benefit of later data, the
/// hazard `GateRecompute` guards with its own truncation.
///
/// **The member band depends on the SESSION, not the subject**, so it is computed once per (session, band
/// definition) and shared across every subject — 200 members × ~191 sessions of 63-point fits, once each,
/// rather than once per subject per session.
/// </summary>
public sealed class BandInputs
{
    private readonly Dictionary<string, AlignedSeries> _subjects = new(StringComparer.Ordinal);
    private readonly List<AlignedSeries> _members = [];
    private readonly Dictionary<(string AsOf, double Low, double High), (double Lo, double Hi)?> _bandCache = [];

    /// <summary>One curve's returns against the benchmark, with the as-of of each return, so a session can
    /// be located by binary search rather than by re-filtering the series.</summary>
    private sealed record AlignedSeries(List<string> Dates, List<double> Strat, List<double> Bench);

    private BandInputs() { }

    /// <summary>
    /// Build the inputs for a run. <paramref name="subjects"/> are the strategies being recomputed; the
    /// matched population is resolved through the SAME <see cref="PopulationMatcher"/> the pipeline uses
    /// (6.3), not by a second copy of its predicate. Until then this method carried its own
    /// `Family == "daily" &amp;&amp; CostsOn` query under a comment asserting the two must agree — and
    /// citing a `DailyPipeline` line number that had already moved. A shared resolver removes the
    /// possibility of drift instead of documenting the obligation to avoid it.
    ///
    /// **A MIXED-FAMILY SUBJECT SET IS REFUSED.** The member band is computed once per session and shared
    /// across subjects, which is only sound while every subject shares one null. Once subjects span two
    /// families, one band cannot serve both — so this returns null rather than silently banding a monthly
    /// strategy against daily members, which is the exact mismatch 6.3 exists to remove. Reachable only
    /// after a non-daily strategy registers (6.10/6.11); the recompute harness gains per-family bands
    /// then, and until then this is a guard that cannot fire rather than dead code.
    /// </summary>
    public static BandInputs? Build(
        AlphaLabDbContext db, IReadOnlyCollection<string> subjects, string benchmarkStrategyId, string runKind)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(subjects);

        var benchAccount = db.Accounts.FirstOrDefault(a => a.StrategyId == benchmarkStrategyId && a.RunKind == runKind);
        if (benchAccount is null) return null;
        var benchCurve = CurveMath.Curve(db, benchAccount.AccountId, runKind);
        if (benchCurve.Count < 2) return null;

        var matcher = PopulationMatcher.ByDeclaration(db);
        var matches = subjects.Select(matcher.For).ToList();
        if (matches.Select(m => m.Family).Distinct(StringComparer.Ordinal).Count() > 1) return null;

        if (matches.Count == 0 || matches[0].PopulationId is not { } pid) return null;

        var inputs = new BandInputs();

        // The subjects' own curves.
        foreach (var strategyId in subjects)
        {
            var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strategyId && a.RunKind == runKind);
            if (account is null) continue;
            var curve = CurveMath.Curve(db, account.AccountId, runKind);
            if (curve.Count < 2) continue;
            if (Align(curve, benchCurve) is { } aligned) inputs._subjects[strategyId] = aligned;
        }

        // The matched population's members — ordered exactly as OverfittingMonitor.PopulationReturns reads
        // them, so the band is built from the same set in the same way.
        var members = db.ControlEquity
            .Where(e => e.PopulationId == pid && e.RunKind == runKind)
            .OrderBy(e => e.MemberIndex).ThenBy(e => e.AsOf)
            .Select(e => new { e.MemberIndex, e.AsOf, e.Equity })
            .AsEnumerable()
            .GroupBy(e => e.MemberIndex);

        foreach (var member in members)
        {
            var curve = member.Select(e => (e.AsOf, e.Equity)).ToList();
            if (Align(curve, benchCurve) is { } aligned) inputs._members.Add(aligned);
        }

        return inputs;
    }

    /// <summary>The central band of the members' window alphas at <paramref name="asOf"/>. Null when no
    /// member has a full window yet — the monitor's own `memberWindowAlphas.Count > 0` precondition.</summary>
    public (double Lo, double Hi)? MemberBand(string asOf, double lowPct, double highPct)
    {
        var key = (asOf, lowPct, highPct);
        if (_bandCache.TryGetValue(key, out var cached)) return cached;

        var alphas = new List<double>(_members.Count);
        foreach (var m in _members)
        {
            if (Window(m, asOf) is { } w) alphas.Add(OverfittingMonitor.SafeAlpha(w.Strat, w.Bench).Alpha);
        }

        (double, double)? band = alphas.Count == 0
            ? null
            : (Statistics.Percentile(alphas, lowPct), Statistics.Percentile(alphas, highPct));
        _bandCache[key] = band;
        return band;
    }

    /// <summary>The subject's own 63-day window alpha and t-stat at <paramref name="asOf"/>; null when its
    /// track is shorter than the window, which is the monitor's `insufficient_track` outcome.</summary>
    public (double Alpha, double T)? StrategyWindow(string strategyId, string asOf) =>
        _subjects.TryGetValue(strategyId, out var s) && Window(s, asOf) is { } w
            ? OverfittingMonitor.SafeAlpha(w.Strat, w.Bench)
            : null;

    // ---- helpers ---------------------------------------------------------------------------------------

    private static AlignedSeries? Align(
        List<(string AsOf, decimal Equity)> curve, List<(string AsOf, decimal Equity)> benchCurve)
    {
        // AlignedReturns drops the first common date and any prev<=0 step, so the RETURN at index i belongs
        // to the (i+1)-th common date. Rebuild that date list on the same rule so a session maps to the
        // right slice — an off-by-one here would silently window the wrong days.
        var benchByDate = new Dictionary<string, decimal>(benchCurve.Count, StringComparer.Ordinal);
        foreach (var (asOf, equity) in benchCurve) benchByDate[asOf] = equity;

        var common = curve.Where(c => benchByDate.ContainsKey(c.AsOf)).ToList();
        var dates = new List<string>(Math.Max(0, common.Count - 1));
        for (var i = 1; i < common.Count; i++)
        {
            if (common[i - 1].Equity <= 0m || benchByDate[common[i - 1].AsOf] <= 0m) continue;
            dates.Add(common[i].AsOf);
        }

        var (strat, bench) = CurveMath.AlignedReturns(curve, benchCurve);
        return dates.Count == strat.Count && strat.Count >= 2 ? new AlignedSeries(dates, strat, bench) : null;
    }

    /// <summary>The last <see cref="OverfittingMonitor.RollingWindowDays"/> returns at or before
    /// <paramref name="asOf"/>. Null when the window is not yet full — matching the monitor, which emits
    /// `insufficient_track` rather than judging a partial window.</summary>
    private static (List<double> Strat, List<double> Bench)? Window(AlignedSeries s, string asOf)
    {
        // Dates are ascending; find the count at or before the session.
        var hi = s.Dates.Count;
        var lo = 0;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (string.CompareOrdinal(s.Dates[mid], asOf) <= 0) lo = mid + 1; else hi = mid;
        }
        var available = lo;
        const int window = OverfittingMonitor.RollingWindowDays;
        if (available < window) return null;

        return (s.Strat.GetRange(available - window, window), s.Bench.GetRange(available - window, window));
    }
}
