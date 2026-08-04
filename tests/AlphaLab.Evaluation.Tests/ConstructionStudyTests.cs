using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Core.Signals;
using AlphaLab.Evaluation.Construction;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Power;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// An <see cref="IFeatureView"/> over a hand-built (security × session) price panel, carrying the SAME
/// point-in-time guard as the real <c>BarFeatureView</c>: a read for a date after <see cref="AsOf"/>
/// THROWS rather than returning null. That is the half that makes the leakage fixture meaningful —
/// returning null would be indistinguishable from thin history, which the scorers are specifically
/// written to tolerate, so a leak would be silently absorbed instead of failing the test.
/// </summary>
internal sealed class PanelFakeView(
    DateOnly asOf, IReadOnlyList<DateOnly> sessions, Dictionary<long, double?[]> prices) : IFeatureView
{
    public DateOnly AsOf => asOf;
    public string Watermark => "2026-08-01T00:00:00Z";

    private int IndexOf(DateOnly d)
    {
        for (var i = 0; i < sessions.Count; i++) if (sessions[i] == d) return i;
        return -1;
    }

    private void GuardNotFuture(DateOnly date)
    {
        if (date > asOf)
        {
            throw new ArgumentOutOfRangeException(
                nameof(date), date, $"Point-in-time violation (rule 4): view is as-of {asOf}, asked for {date}.");
        }
    }

    private double? Price(long id, int i) =>
        i >= 0 && prices.TryGetValue(id, out var row) && i < row.Length ? row[i] : null;

    public IReadOnlyList<SecurityId> PricedOn(DateOnly date)
    {
        GuardNotFuture(date);
        var i = IndexOf(date);
        return prices.Keys.Where(id => Price(id, i) is { } p && p > 0)
            .OrderBy(id => id).Select(id => new SecurityId(id)).ToList();
    }

    public double? AdjClose(SecurityId id, DateOnly date)
    {
        GuardNotFuture(date);
        return Price(id.Value, IndexOf(date));
    }

    public double? RawClose(SecurityId id, DateOnly date) => AdjClose(id, date);
    public double? RawOpen(SecurityId id, DateOnly date) => AdjClose(id, date);

    /// <summary>The prices present in the last <paramref name="count"/> CALENDAR sessions ending at
    /// AsOf — BarFeatureView's convention, which counts sessions rather than available bars.</summary>
    public IReadOnlyList<double> AdjCloseSeries(SecurityId id, int count)
    {
        var end = IndexOf(asOf);
        if (end < 0) return [];
        var start = Math.Max(0, end - count + 1);
        var outp = new List<double>();
        for (var i = start; i <= end; i++) if (Price(id.Value, i) is { } p) outp.Add(p);
        return outp;
    }

    public double? Adv21Shares(SecurityId id) => 1_000_000.0;
    public double? Adv21Notional(SecurityId id) => 5.0e8;          // mega bucket
    public double? RealizedVolDaily(SecurityId id, int window)
    {
        var s = AdjCloseSeries(id, window + 1);
        return s.Count < window + 1 ? null : PriceStatistics.RealizedVolDaily(s);
    }
}

/// <summary>A scorer whose ranking is fixed and known, so a fixture can assert the exact tail cut
/// rather than whatever the real signals happen to produce.</summary>
internal sealed class FixedScoreSignal(IReadOnlyDictionary<long, double> scores) : ISignal
{
    public string SignalId => "fixture:fixed";
    public string Family => "fixture";
    public string CodeVersion => "test";
    public IReadOnlyDictionary<string, double> Params { get; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<SecurityId, double> Score(
        IReadOnlyList<SecurityId> eligible, IFeatureView features, SignalContext context) =>
        eligible.Where(id => scores.ContainsKey(id.Value))
            .ToDictionary(id => id, id => scores[id.Value]);
}

/// <summary>
/// FX-Construction* (Phase 5.5 / D123): the construction study's arithmetic and its point-in-time
/// discipline. The study exists to inform whether the lab builds shorting, so every number it reports
/// must be reproducible by hand and provably free of hindsight — a study that peeks is worse than no
/// study, because it would argue for weeks of work on a number that cannot be true.
/// </summary>
public class ConstructionStudyTests
{
    private static readonly GateOptions Gate = new();
    private static readonly CostsOptions Costs = new();

    private static List<DateOnly> Sessions(int count, DateOnly start)
    {
        // Weekdays only — enough calendar realism for the monthly-rebalance rule to be exercised.
        var outp = new List<DateOnly>(count);
        var d = start;
        while (outp.Count < count)
        {
            if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) outp.Add(d);
            d = d.AddDays(1);
        }
        return outp;
    }

    /// <summary>A geometric ramp per security: P = start·(1+g)^i. Closed form, so expected returns are
    /// arithmetic a reader can check.</summary>
    private static Dictionary<long, double?[]> Ramps(IReadOnlyList<(long Id, double G)> spec, int sessions)
    {
        var panel = new Dictionary<long, double?[]>();
        foreach (var (id, g) in spec)
        {
            var row = new double?[sessions];
            for (var i = 0; i < sessions; i++) row[i] = 100.0 * Math.Pow(1.0 + g, i);
            panel[id] = row;
        }
        return panel;
    }

    private static ConstructionStudyEngine Engine(
        IReadOnlyList<DateOnly> sessions, Dictionary<long, double?[]> prices, ConstructionStudyOptions? opts = null)
    {
        var panel = new AdjClosePanel(sessions, prices);
        var ids = prices.Keys.OrderBy(x => x).Select(x => new SecurityId(x)).ToList();
        return new ConstructionStudyEngine(
            panel,
            asOf => new PanelFakeView(asOf, sessions, prices),
            _ => ids,
            new CostModel(Costs),
            Gate,
            opts ?? new ConstructionStudyOptions());
    }

    // ---- FX-ConstructionRebalanceCalendar ----

    [Fact]
    public void FR47_ConstructionStudy_RebalancesOnFirstSessionOfEachMonth()
    {
        var sessions = Sessions(70, new DateOnly(2020, 1, 1));
        var idx = ConstructionStudyEngine.MonthlyRebalanceIndices(sessions);

        // Index 0 always, then every session whose month differs from its predecessor.
        Assert.Contains(0, idx);
        foreach (var i in idx.Where(i => i > 0))
        {
            Assert.NotEqual(sessions[i - 1].Month, sessions[i].Month);
        }
        // And nothing else rebalances.
        for (var i = 1; i < sessions.Count; i++)
        {
            if (sessions[i].Month == sessions[i - 1].Month) Assert.DoesNotContain(i, idx);
        }
    }

    // ---- FX-ConstructionTails: the cut is deterministic, and ties break on security_id ----

    [Fact]
    public void FR47_ConstructionStudy_TailCutIsDeterministicUnderTies()
    {
        var sessions = Sessions(60, new DateOnly(2020, 1, 1));
        // Ten names. Scores 1..5 are distinct; 6..10 all tie at 0.0 — the brk:L252 saturation shape.
        var scores = new Dictionary<long, double>
        {
            [1] = 5.0, [2] = 4.0, [3] = 3.0, [4] = 2.0, [5] = 1.0,
            [6] = 0.0, [7] = 0.0, [8] = 0.0, [9] = 0.0, [10] = 0.0,
        };
        var prices = Ramps([.. scores.Keys.Select(k => (k, 0.001))], sessions.Count);
        var opts = new ConstructionStudyOptions { TailFraction = 0.20 };   // k = 2 of 10

        var a = Engine(sessions, prices, opts).Measure(new FixedScoreSignal(scores), new SignalContext());
        var b = Engine(sessions, prices, opts).Measure(new FixedScoreSignal(scores), new SignalContext());

        Assert.Equal(2.0, a.MeanTailSize);
        Assert.Equal(10.0, a.MeanScoredNames);
        // Byte-identical across runs — without the security_id tie-break the tied block could cut
        // differently each time and the study would not be reproducible.
        Assert.Equal(a.LongOnly.SigmaLrDaily, b.LongOnly.SigmaLrDaily);
        Assert.Equal(a.LongShort.GrossEffectAnn, b.LongShort.GrossEffectAnn);
    }

    // ---- FX-ConstructionFloor: floor = ZSum · TE / sqrt(H) ----

    [Fact]
    public void FR47_ConstructionStudy_FloorIsZSumTimesTeOverSqrtHorizon()
    {
        var sessions = Sessions(300, new DateOnly(2019, 1, 1));
        var spec = new List<(long, double)>();
        for (long id = 1; id <= 10; id++) spec.Add((id, 0.0002 * id));   // fanned growth rates
        var prices = Ramps(spec, sessions.Count);
        var scores = spec.ToDictionary(s => s.Item1, s => (double)s.Item1);

        var m = Engine(sessions, prices, new ConstructionStudyOptions { TailFraction = 0.20 })
            .Measure(new FixedScoreSignal(scores), new SignalContext());

        foreach (var leg in new[] { m.LongOnly, m.LongShort })
        {
            // TE is sigma_LR annualised...
            Assert.Equal(leg.SigmaLrDaily * Math.Sqrt(252.0), leg.TrackingErrorAnn, 12);

            // ...and the floor is the gate's own expression, which reduces to ZSum·TE/sqrt(H).
            var expected = MdeCalculator.ZSum(Gate.Confidence, Gate.Power)
                           * leg.TrackingErrorAnn / Math.Sqrt(Gate.DetectabilityHorizonYears);
            Assert.Equal(expected, leg.FloorAnn, 12);
        }
    }

    // ---- FX-ConstructionPit: the selection cannot see the future (rule 4) ----

    [Fact]
    public void FR47_ConstructionStudy_SelectionIsPointInTime()
    {
        var sessions = Sessions(200, new DateOnly(2020, 1, 1));
        var spec = new List<(long, double)>();
        for (long id = 1; id <= 10; id++) spec.Add((id, 0.0003 * id));
        var scores = spec.ToDictionary(s => s.Item1, s => (double)s.Item1);
        var opts = new ConstructionStudyOptions { TailFraction = 0.20 };

        var baseline = Ramps(spec, sessions.Count);
        var scrambled = Ramps(spec, sessions.Count);
        // Detonate the FINAL session's prices. Every rebalance happens strictly before it, so a
        // point-in-time engine must produce identical selections — only the last realised return moves.
        foreach (var id in scrambled.Keys.ToList())
        {
            scrambled[id][sessions.Count - 1] = 1.0;
        }

        var a = Engine(sessions, baseline, opts).Measure(new FixedScoreSignal(scores), new SignalContext());
        var b = Engine(sessions, scrambled, opts).Measure(new FixedScoreSignal(scores), new SignalContext());

        Assert.Equal(a.Rebalances, b.Rebalances);
        Assert.Equal(a.MeanTailSize, b.MeanTailSize);
        Assert.Equal(a.MeanScoredNames, b.MeanScoredNames);
        // Same number of observations, and the cost drag — a pure function of the selections — is
        // untouched by anything that happened after the last rebalance.
        Assert.Equal(a.LongOnly.Observations, b.LongOnly.Observations);
        Assert.Equal(a.LongOnly.CostDragAnn, b.LongOnly.CostDragAnn, 12);
        Assert.Equal(a.LongShort.CostDragAnn, b.LongShort.CostDragAnn, 12);
    }

    /// <summary>A view asked about a future date THROWS — the guard the leakage argument rests on. If
    /// this ever returned null instead, a leak would present as thin history and be absorbed silently.</summary>
    [Fact]
    public void FR47_ConstructionStudy_FakeViewRefusesFutureReads()
    {
        var sessions = Sessions(10, new DateOnly(2020, 1, 1));
        var prices = Ramps([(1L, 0.001)], sessions.Count);
        var view = new PanelFakeView(sessions[3], sessions, prices);

        Assert.Equal(100.0 * Math.Pow(1.001, 3), view.AdjClose(new SecurityId(1), sessions[3])!.Value, 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.AdjClose(new SecurityId(1), sessions[4]));
    }

    // ---- FX-ConstructionBasket: an unpriced name renormalises, it does not zero the day ----

    [Fact]
    public void FR47_ConstructionStudy_UnpricedNameDropsFromBasketRatherThanScoringZero()
    {
        var sessions = Sessions(5, new DateOnly(2020, 1, 1));
        var prices = new Dictionary<long, double?[]>
        {
            [1] = [100, 110, null, 121, 121],   // +10% then a gap
            [2] = [100, 100, 100, 100, 100],    // flat
        };
        var panel = new AdjClosePanel(sessions, prices);
        var basket = new List<SecurityId> { new(1), new(2) };

        // Session 0→1: both priced. EW of (+10%, 0%) = +5%.
        Assert.Equal(0.05, panel.EqualWeightReturn(basket, 0, 1)!.Value, 12);

        // Session 1→2: name 1 is unpriced, so the basket is name 2 alone — 0%, NOT −50% from a
        // fabricated zero price.
        Assert.Equal(0.0, panel.EqualWeightReturn(basket, 1, 2)!.Value, 12);

        // Nothing priced at either end ⇒ no observation at all.
        Assert.Null(panel.EqualWeightReturn([new SecurityId(99)], 0, 1));
    }

    // ---- FX-ConstructionBorrow: borrow hits the short leg only, and only the net effect ----

    [Fact]
    public void FR47_ConstructionStudy_BorrowAppliesToShortLegOnlyAndNeverToTrackingError()
    {
        var sessions = Sessions(300, new DateOnly(2019, 1, 1));
        var spec = new List<(long, double)>();
        for (long id = 1; id <= 10; id++) spec.Add((id, 0.0002 * id));
        var prices = Ramps(spec, sessions.Count);
        var scores = spec.ToDictionary(s => s.Item1, s => (double)s.Item1);

        var m = Engine(sessions, prices, new ConstructionStudyOptions
        {
            TailFraction = 0.20,
            BorrowBpPerYear = [0.0, 40.0],
        }).Measure(new FixedScoreSignal(scores), new SignalContext());

        // The long-only leg carries exactly one net row, at 0 bp: nothing is borrowed.
        var lo = Assert.Single(m.LongOnly.NetEffects);
        Assert.Equal(0.0, lo.BorrowBpPerYear);

        // The long-short leg carries both assumptions, and they differ by exactly 40 bp.
        Assert.Equal(2, m.LongShort.NetEffects.Count);
        Assert.Equal(0.0, m.LongShort.NetEffects[0].BorrowBpPerYear);
        Assert.Equal(40.0, m.LongShort.NetEffects[1].BorrowBpPerYear);
        Assert.Equal(
            0.0040,
            m.LongShort.NetEffects[0].NetEffectAnn - m.LongShort.NetEffects[1].NetEffectAnn,
            12);

        // Borrow is a drag on the MEAN and never enters the series, so it cannot move tracking error —
        // which is the number the whole study turns on.
        var noBorrow = Engine(sessions, prices, new ConstructionStudyOptions
        {
            TailFraction = 0.20,
            BorrowBpPerYear = [0.0],
        }).Measure(new FixedScoreSignal(scores), new SignalContext());
        Assert.Equal(m.LongShort.TrackingErrorAnn, noBorrow.LongShort.TrackingErrorAnn, 12);
        Assert.Equal(m.LongShort.FloorAnn, noBorrow.LongShort.FloorAnn, 12);
    }

    /// <summary>
    /// A deterministic one-factor panel: <c>P_{j,i} = P_{j,i−1}·(1 + drift_j + β_j·m_i + σ_j·ε_{j,i})</c>.
    ///
    /// GEOMETRIC RAMPS WILL NOT DO, and the reason is worth recording because the first version of the
    /// all-seven fixture used them and PASSED WITHOUT MEASURING ANYTHING. On a pure ramp every daily
    /// return is identical, so the market's return variance is ~1e-32 — floating-point residue. `bab` and
    /// `resmom` divide by it and get noise rather than a beta; `lowvol` sees zero volatility everywhere;
    /// `brk` sees every name at its own high and scores 1.0 for all of them. Four of the seven signals
    /// were degenerate and the assertions — "finite, non-negative, non-empty" — were all satisfied anyway.
    /// A factor panel gives each of the seven something real to rank on.
    /// </summary>
    private static Dictionary<long, double?[]> FactorPanel(int names, int sessions, long proxyId, int seed)
    {
        // A plain LCG + Box–Muller: reproducible without depending on Random's unspecified sequence.
        var state = (ulong)seed;
        double NextUniform()
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            return ((state >> 11) & ((1UL << 53) - 1)) / (double)(1UL << 53);
        }
        double NextNormal()
        {
            var u1 = Math.Max(1e-12, NextUniform());
            var u2 = NextUniform();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        var market = new double[sessions];
        for (var i = 0; i < sessions; i++) market[i] = 0.0002 + 0.010 * NextNormal();

        var panel = new Dictionary<long, double?[]>();
        for (long j = 1; j <= names; j++)
        {
            var beta = 0.5 + 1.0 * (j - 1) / Math.Max(1, names - 1);       // 0.5 .. 1.5
            var idio = 0.005 + 0.020 * NextUniform();                       // real vol dispersion
            var drift = -0.0003 + 0.0006 * (j - 1) / Math.Max(1, names - 1);
            var row = new double?[sessions];
            var p = 100.0;
            for (var i = 0; i < sessions; i++)
            {
                if (i > 0) p *= 1.0 + drift + beta * market[i] + idio * NextNormal();
                row[i] = p;
            }
            panel[j] = row;
        }

        // The proxy itself compounds the market factor, so beta against it is meaningful.
        var proxyRow = new double?[sessions];
        var mp = 100.0;
        for (var i = 0; i < sessions; i++)
        {
            if (i > 0) mp *= 1.0 + market[i];
            proxyRow[i] = mp;
        }
        panel[proxyId] = proxyRow;
        return panel;
    }

    // ---- FX-ConstructionScaleFree: the correction the study forced on its own design ----

    /// <summary>
    /// The information ratio is scale-free and years-to-detect follows from it alone — so LEVERAGE
    /// cannot make anything more detectable.
    ///
    /// This fixture exists because the first version of the report compared the two constructions' raw
    /// detectability FLOORS, and that comparison is empty: a long-short book is ~2x leverage on the same
    /// cross-sectional bet, so it scales TE and effect together and the floor moves with both. The live
    /// smoke run showed it outright — `resmom:L252` came back TE x3.01, effect x3.01, IR 0.374 -> 0.373.
    /// The assertion below is the arithmetic that makes the mistake impossible to reintroduce.
    /// </summary>
    [Fact]
    public void FR47_ConstructionStudy_InformationRatioIsScaleFreeSoLeverageBuysNoDetectability()
    {
        const double Zsum = 2.8016;   // 95% / 80%, matching Gate's defaults
        Assert.Equal(Zsum, MdeCalculator.ZSum(Gate.Confidence, Gate.Power), 4);

        // A construction that doubles BOTH effect and tracking error leaves IR — and therefore the track
        // length needed to resolve it — exactly where it was.
        const double Effect = 0.04, Te = 0.16;
        var ir1 = Effect / Te;
        var ir2 = 2 * Effect / (2 * Te);
        Assert.Equal(ir1, ir2, 12);
        Assert.Equal(Math.Pow(Zsum / ir1, 2), Math.Pow(Zsum / ir2, 2), 9);

        // ...while the FLOOR doubles, which is why comparing floors across constructions says nothing.
        Assert.Equal(2.0, (Zsum * (2 * Te) / Math.Sqrt(10)) / (Zsum * Te / Math.Sqrt(10)), 12);
    }

    /// <summary>Years-to-detect is `(ZSum/IR)^2` off the NET effect, and a zero effect is never
    /// detectable — infinity rather than a large finite number a reader could mistake for "eventually".</summary>
    [Fact]
    public void FR47_ConstructionStudy_YearsToDetectFollowsTheNetEffectAndIsInfiniteAtZero()
    {
        var sessions = Sessions(300, new DateOnly(2019, 1, 1));
        var spec = new List<(long, double)>();
        for (long id = 1; id <= 10; id++) spec.Add((id, 0.0002 * id));
        var prices = Ramps(spec, sessions.Count);
        var scores = spec.ToDictionary(s => s.Item1, s => (double)s.Item1);

        var m = Engine(sessions, prices, new ConstructionStudyOptions { TailFraction = 0.20 })
            .Measure(new FixedScoreSignal(scores), new SignalContext());

        var z = MdeCalculator.ZSum(Gate.Confidence, Gate.Power);
        foreach (var leg in new[] { m.LongOnly, m.LongShort })
        {
            foreach (var n in leg.NetEffects)
            {
                Assert.Equal(Math.Abs(n.NetEffectAnn) / leg.TrackingErrorAnn, n.InformationRatio, 12);
                Assert.Equal(Math.Pow(z / n.InformationRatio, 2), n.YearsToDetect, 9);
            }
        }

        // A leg with no measured effect is never detectable at any horizon.
        var flat = new ConstructionStudyOptions { TailFraction = 0.20 };
        var zeroLeg = Engine(sessions, prices, flat).Measure(new FixedScoreSignal(scores), new SignalContext());
        Assert.All(zeroLeg.LongShort.NetEffects, n =>
            Assert.True(double.IsFinite(n.YearsToDetect) || n.InformationRatio == 0.0));
    }

    // ---- every registered signal is measurable: the per-signal check, made executable ----

    [Fact]
    public void FR47_ConstructionStudy_MeasuresAllSevenRegisteredSignals()
    {
        // 400 sessions clears mom:L252s21's 274-session need; 20 names give a 2-name decile.
        const int Count = 400;
        const long ProxyId = 999L;
        var sessions = Sessions(Count, new DateOnly(2018, 1, 2));
        var prices = FactorPanel(names: 20, sessions: Count, proxyId: ProxyId, seed: 20260803);
        var context = new SignalContext(new SecurityId(ProxyId));

        var seen = new List<SignalMeasurement>();
        foreach (var signal in SignalRegistry.V1)
        {
            var m = Engine(sessions, prices).Measure(signal, context);

            Assert.Equal(signal.SignalId, m.SignalId);
            // resmom/bab need the proxy, which the context supplies — a null proxy leaves them scoring
            // nothing at all, and silent emptiness is exactly what these assertions exist to catch.
            Assert.True(m.Rebalances > 0, $"{signal.SignalId}: no rebalance produced a book.");
            Assert.True(m.LongOnly.Observations > 0, $"{signal.SignalId}: long-only has no observations.");
            Assert.True(m.LongShort.Observations > 0, $"{signal.SignalId}: long-short has no observations.");

            foreach (var leg in new[] { m.LongOnly, m.LongShort })
            {
                Assert.True(double.IsFinite(leg.FloorAnn), $"{signal.SignalId}/{leg.Construction}: floor not finite.");
                Assert.True(double.IsFinite(leg.TrackingErrorAnn), $"{signal.SignalId}/{leg.Construction}: TE not finite.");
                // STRICTLY positive: a zero TE means the two baskets moved identically every day, which on
                // a factor panel means the signal never actually separated them — the degenerate pass the
                // ramp version allowed through.
                Assert.True(leg.TrackingErrorAnn > 0, $"{signal.SignalId}/{leg.Construction}: TE is zero — tails never separated.");
                Assert.True(leg.FloorAnn > 0, $"{signal.SignalId}/{leg.Construction}: floor is zero.");
            }
            seen.Add(m);
        }

        Assert.Equal(7, seen.Count);
        // The seven must be DISTINGUISHABLE. If every signal cut the same tail, the study could not
        // report anything per-signal and the panel would be telling us nothing.
        var distinctTe = seen.Select(m => Math.Round(m.LongShort.TrackingErrorAnn, 6)).Distinct().Count();
        Assert.True(distinctTe >= 5, $"only {distinctTe} distinct long-short TEs across 7 signals — tails are not separating.");
    }
}
