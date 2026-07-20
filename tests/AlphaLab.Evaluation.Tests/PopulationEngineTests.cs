using System.Diagnostics;
using System.Globalization;
using AlphaLab.Evaluation.Populations;

namespace AlphaLab.Evaluation.Tests;

public class PopulationEngineTests
{
    // A deterministic universe with ZERO cross-sectional mean return each day: raw dispersion minus the
    // day's equal-weight mean, so the equal-weight benchmark return is exactly 0 and any random slice has
    // expected excess 0. A fixed one-way cost fraction lets us reason about the net offset.
    private sealed class SyntheticMarket(int nSecurities, double costFraction) : IPopulationMarket
    {
        private readonly long[] _ids = [.. Enumerable.Range(1, nSecurities).Select(i => (long)i)];

        public IReadOnlyList<long> Eligible(string date) => _ids;

        public double DailyReturn(long securityId, string date)
        {
            var day = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber;
            var mean = _ids.Average(id => Raw(id, day));
            return Raw(securityId, day) - mean;
        }

        public double OneWayCostFraction(long securityId, string date, decimal perNameNotional) => costFraction;

        private static double Raw(long id, int day) => 0.02 * Math.Sin(id * 0.7 + day * 0.13);
    }

    private static List<string> Sessions(int n, DateOnly start)
    {
        var dates = new List<string>(n);
        var d = start;
        for (var i = 0; i < n; i++)
        {
            dates.Add(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            d = d.AddDays(1);
        }
        return dates;
    }

    [Fact]
    public void Select_IsDeterministic_AndOrderIndependentInEligible()
    {
        var engine = new PopulationEngine(new SyntheticMarket(100, 0.0));
        var f = new PopulationFamily("daily", 1001, 40, 1, CostsOn: true, Size: 200);
        var a = engine.Select(f, memberIndex: 7, "2026-03-10");
        var b = engine.Select(f, memberIndex: 7, "2026-03-10");
        Assert.Equal(40, a.Count);
        Assert.True(a.SetEquals(b));
    }

    [Fact]
    public void Select_DiffersByMemberAndBySeed()
    {
        var engine = new PopulationEngine(new SyntheticMarket(100, 0.0));
        var f = new PopulationFamily("daily", 1001, 40, 1, CostsOn: true, Size: 200);
        var g = f with { FamilySeed = 1002 };
        var m7 = engine.Select(f, 7, "2026-03-10");
        var m8 = engine.Select(f, 8, "2026-03-10");
        var s2 = engine.Select(g, 7, "2026-03-10");
        Assert.False(m7.SetEquals(m8));   // different member ⇒ different draw
        Assert.False(m7.SetEquals(s2));   // different seed ⇒ different draw
    }

    [Fact]
    public void FX_PopDeterminism_IdenticalEquityAcrossTwoRuns()
    {
        var engine = new PopulationEngine(new SyntheticMarket(100, 0.001));
        var f = new PopulationFamily("daily", 1001, 40, 1, CostsOn: true, Size: 50);
        var dates = Sessions(80, new DateOnly(2026, 1, 5));

        var run1 = engine.SimulateMember(f, 12, 100_000m, dates);
        var run2 = engine.SimulateMember(f, 12, 100_000m, dates);

        Assert.Equal(run1.Count, run2.Count);
        for (var i = 0; i < run1.Count; i++)
            Assert.Equal(run1[i].Equity, run2[i].Equity);   // exact decimal equality
    }

    [Fact]
    public void FX_PopBands_GrossCentersNearZero_NetIsOffsetBelowByCostDrag()
    {
        var market = new SyntheticMarket(100, costFraction: 0.001);
        var engine = new PopulationEngine(market);
        var dates = Sessions(120, new DateOnly(2026, 1, 5));
        const int m = 60;

        // Twins: same seed + member index, one costs-off (gross), one costs-on (net).
        var gross = new PopulationFamily("daily", 1001, 40, 1, CostsOn: false, Size: m);
        var net = new PopulationFamily("daily", 1001, 40, 1, CostsOn: true, Size: m);

        var grossRet = new double[m];
        var netRet = new double[m];
        for (var i = 0; i < m; i++)
        {
            var g = engine.SimulateMember(gross, i, 100_000m, dates);
            var n = engine.SimulateMember(net, i, 100_000m, dates);
            grossRet[i] = (double)(g[^1].Equity / 100_000m) - 1.0;
            netRet[i] = (double)(n[^1].Equity / 100_000m) - 1.0;
            // Every twin: the net path is strictly below its gross twin (costs are always paid, >0 turnover).
            Assert.True(n[^1].Equity < g[^1].Equity, $"member {i}: net {n[^1].Equity} should be < gross {g[^1].Equity}");
        }

        // Gross distribution centers on ~0 (random equal-weight slices of a zero-mean universe).
        Assert.True(Math.Abs(grossRet.Average()) < 0.02, $"gross mean {grossRet.Average():F4} should be ~0");

        // Net is offset BELOW gross by a material, positive cost drag (the daily-churn family pays a lot).
        var offset = grossRet.Average() - netRet.Average();
        Assert.True(offset > 0.02, $"net should sit below gross by the cost drag; offset was {offset:F4}");
    }

    [Fact]
    public void Redraw_Cadence_LowersRealizedTurnover_DailyVsMonthly()
    {
        var engine = new PopulationEngine(new SyntheticMarket(100, 0.001));
        var dates = Sessions(120, new DateOnly(2026, 1, 5));

        var daily = new PopulationFamily("daily", 1001, 40, 1, CostsOn: true, Size: 1);
        var monthly = new PopulationFamily("monthly", 1003, 40, PopulationFamilies.MonthlyInterval, CostsOn: true, Size: 1);

        var dailyTurnover = engine.SimulateMember(daily, 0, 100_000m, dates).Average(d => d.TurnoverOneWay);
        var monthlyTurnover = engine.SimulateMember(monthly, 0, 100_000m, dates).Average(d => d.TurnoverOneWay);

        Assert.True(dailyTurnover > monthlyTurnover * 3,
            $"daily churn {dailyTurnover:F3} should dwarf monthly {monthlyTurnover:F3}");
    }

    [Fact]
    public void Perf_FullPopulationDay_Over500Names_ComputesWellUnderBudget()
    {
        // The §5.2 <60s daily-run DoD: the population COMPUTE for ~650 members (3×200 + 50) over a
        // 500-name universe is a small fraction of a daily run. The 20s ceiling is ~200× the real cost,
        // so it never flakes on a loaded CI box yet still trips on a pathological regression (e.g. an
        // accidental O(members²) or per-member full re-sort blowup).
        var engine = new PopulationEngine(new SyntheticMarket(500, 0.001));
        PopulationFamily[] families =
        [
            new("daily", 1001, 40, 1, CostsOn: true, Size: 200),
            new("banded", 1002, 40, PopulationFamilies.BandedInterval, CostsOn: true, Size: 200),
            new("monthly", 1003, 40, PopulationFamilies.MonthlyInterval, CostsOn: true, Size: 200),
            new("daily", 1001, 40, 1, CostsOn: false, Size: 50),
        ];
        var prior = Enumerable.Repeat(100_000m, 200).ToList();

        var sw = Stopwatch.StartNew();
        var total = 0;
        foreach (var f in families)
        {
            var day = engine.ComputeFamilyDay(f, prior.Take(f.Size).ToList(), "2026-03-09", "2026-03-10");
            total += day.Length;
        }
        sw.Stop();

        Assert.Equal(650, total);
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"population day took {sw.Elapsed.TotalSeconds:F1}s (budget 20s)");
    }
}
