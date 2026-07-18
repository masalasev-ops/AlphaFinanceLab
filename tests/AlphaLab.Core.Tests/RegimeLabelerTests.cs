using AlphaLab.Core.Regime;

namespace AlphaLab.Core.Tests;

/// <summary>
/// FX-RegimeHysteresis + the pure D50/§20.1 label math (FR-26). The labeler is BCL-pure, so these run
/// with tiny warm-ups and no SQLite. The hysteresis behaviour — oscillation ⇒ zero flips, a sustained
/// breakout ⇒ exactly one flip after the confirmation window — is the whole reason the label exists in
/// this form: without it a market riding its SMA manufactures dozens of spurious episodes (§20.1).
/// </summary>
public class RegimeLabelerTests
{
    private static readonly DateOnly Start = new(2015, 1, 1);
    private static string D(int i) => Start.AddDays(i).ToString("yyyy-MM-dd");

    private static IReadOnlyList<ProxyClose> Series(IReadOnlyList<double> closes)
    {
        var s = new List<ProxyClose>(closes.Count);
        for (var i = 0; i < closes.Count; i++) s.Add(new ProxyClose(D(i), closes[i]));
        return s;
    }

    // Small trend params so the SMA warm-up is 5, not 200. hyst 1%, confirm 5 — the production shape.
    private static RegimeLabelParams TrendParams(int sma = 5, double hyst = 1.0, int confirm = 5) =>
        new(trendSmaDays: sma, trendHysteresisPct: hyst, confirmDays: confirm,
            volWindowDays: 2, volPercentile: 80, volLookbackSessions: 3);

    // ---- FX-RegimeHysteresis: ±0.5% oscillation around the SMA ⇒ ZERO flips ----
    [Fact]
    public void FR26_TrendHysteresis_OscillationAroundSma_ProducesZeroFlips()
    {
        // 35 sessions alternating 100 ± 0.5% — never ≥ 1% beyond the ~100 SMA, so no flip is ever confirmed.
        var closes = new List<double>();
        for (var i = 0; i < 35; i++) closes.Add(100.0 + (i % 2 == 0 ? 0.5 : -0.5));

        var trend = RegimeLabeler.TrendSeries(Series(closes), TrendParams());

        Assert.NotEmpty(trend);
        Assert.Single(trend.Select(t => t.Trend).Distinct()); // one label value across the whole trajectory
    }

    // ---- FX-RegimeHysteresis: a sustained breakout flips EXACTLY ONCE, and only on the ConfirmDays-th session ----
    [Fact]
    public void FR26_TrendHysteresis_SustainedBreakout_FlipsExactlyOnceAfterConfirmDays()
    {
        // 10 flat sessions (seed bear: 100 is not > its own SMA of 100), then a rising ramp that stays
        // ≥ 1% above its trailing SMA every session — so runAbove climbs 1..5 and flips bear→bull on the 5th.
        var closes = new List<double>();
        for (var i = 0; i < 10; i++) closes.Add(100.0);
        var p = 100.0;
        for (var i = 0; i < 8; i++) { p *= 1.02; closes.Add(p); }

        var trend = RegimeLabeler.TrendSeries(Series(closes), TrendParams());

        // Seed is bear; exactly one bear→bull transition; bull thereafter.
        Assert.Equal(RegimeTrend.Bear, trend[0].Trend);
        var flips = 0;
        for (var i = 1; i < trend.Count; i++) if (trend[i].Trend != trend[i - 1].Trend) flips++;
        Assert.Equal(1, flips);
        Assert.Equal(RegimeTrend.Bull, trend[^1].Trend);

        // The flip lands on the 5th consecutive breakout session (index 10 is the 1st ramp; 14 is the 5th).
        var firstBull = trend.First(t => t.Trend == RegimeTrend.Bull);
        Assert.Equal(D(14), firstBull.Date);
    }

    // ---- A breakout SHORTER than ConfirmDays does not flip (the noise the hysteresis is there to reject) ----
    [Fact]
    public void FR26_TrendHysteresis_BreakoutShorterThanConfirmDays_DoesNotFlip()
    {
        // Seed bear, 4 ramp sessions (runAbove 1..4), then a hard drop back to base — the run resets before
        // reaching 5, so the bull flip never confirms.
        var closes = new List<double>();
        for (var i = 0; i < 10; i++) closes.Add(100.0);
        var p = 100.0;
        for (var i = 0; i < 4; i++) { p *= 1.02; closes.Add(p); }   // 4 < confirm(5)
        for (var i = 0; i < 6; i++) closes.Add(100.0);              // drop back

        var trend = RegimeLabeler.TrendSeries(Series(closes), TrendParams());

        Assert.Single(trend.Select(t => t.Trend).Distinct());       // stayed bear throughout — zero flips
        Assert.Equal(RegimeTrend.Bear, trend[^1].Trend);
    }

    // ---- Seed is the RAW comparison (a seed is not a flip, so hysteresis does not gate it) ----
    [Fact]
    public void FR26_TrendSeed_IsTheRawComparison_NoConfirmationRequired()
    {
        // A clean uptrend from the first SMA-computable session: seed must be bull immediately, no 5-day wait.
        var closes = new List<double>();
        var p = 100.0;
        for (var i = 0; i < 12; i++) { closes.Add(p); p *= 1.03; }

        var trend = RegimeLabeler.TrendSeries(Series(closes), TrendParams());
        Assert.Equal(RegimeTrend.Bull, trend[0].Trend); // seeded bull on session 1, not after confirm
    }

    // ---- Vol component: recent vol above its trailing distribution ⇒ high_vol; below ⇒ normal_vol ----
    // The lookback (40) dwarfs the recent stretch (6), mirroring production's 756-vs-21 ratio: a spike that
    // is a small fraction of its own trailing distribution stands out above the 80th percentile. (A short
    // lookback would let the spike drag its own threshold up and hide itself.)
    private static RegimeLabelParams VolParams() =>
        new(trendSmaDays: 3, trendHysteresisPct: 1.0, confirmDays: 3,
            volWindowDays: 2, volPercentile: 80, volLookbackSessions: 40);

    [Fact]
    public void FR26_VolLabel_RecentVolAboveTrailingPercentile_IsHighVol()
    {
        var closes = new List<double> { 100 };
        for (var i = 0; i < 50; i++) closes.Add(closes[^1] * (i % 2 == 0 ? 1.001 : 0.999)); // calm history
        for (var i = 0; i < 6; i++) closes.Add(closes[^1] * (i % 2 == 0 ? 1.05 : 0.95));    // sustained ~5% swings

        var labels = RegimeLabeler.LabelSeries(Series(closes), VolParams());
        Assert.Equal(RegimeVol.HighVol, labels[^1].Vol);
        Assert.Equal("high_vol", labels[^1].VolToken);
    }

    [Fact]
    public void FR26_VolLabel_RecentVolBelowTrailingPercentile_IsNormalVol()
    {
        var closes = new List<double> { 100 };
        for (var i = 0; i < 50; i++) closes.Add(closes[^1] * (i % 2 == 0 ? 1.05 : 0.95));    // volatile history
        for (var i = 0; i < 6; i++) closes.Add(closes[^1] * (i % 2 == 0 ? 1.001 : 0.999));   // then sustained calm

        var labels = RegimeLabeler.LabelSeries(Series(closes), VolParams());
        Assert.Equal(RegimeVol.NormalVol, labels[^1].Vol);
    }

    // ---- The label is the cross product, aligned to dates, produced only where BOTH components exist ----
    [Fact]
    public void FR26_LabelSeries_IsTheCrossProduct_StartingAtTheFirstFullyComputableSession()
    {
        var p = new RegimeLabelParams(trendSmaDays: 3, trendHysteresisPct: 0.0, confirmDays: 1,
            volWindowDays: 2, volPercentile: 50, volLookbackSessions: 3);
        var closes = new List<double>();
        var v = 100.0;
        for (var i = 0; i < 12; i++) { closes.Add(v); v *= 1.01; }

        var labels = RegimeLabeler.LabelSeries(Series(closes), p);

        // First fully-computable index = max(sma-1=2, volWindow+lookback-1=4) = 4 ⇒ label starts at D(4).
        Assert.Equal(D(4), labels[0].Date);
        Assert.Equal(D(11), labels[^1].Date);
        foreach (var l in labels)
        {
            Assert.Contains("/", l.Label);
            Assert.Equal($"{l.TrendToken}/{l.VolToken}", l.Label);
        }
        // A clean uptrend under a zero-band, 1-day confirm ⇒ bull at asOf.
        Assert.Equal(RegimeTrend.Bull, labels[^1].Trend);
    }

    // ---- Too-short series ⇒ empty (the service's readiness guard is the real gate; this is the backstop) ----
    [Fact]
    public void FR26_LabelSeries_ShorterThanWarmup_IsEmpty()
    {
        var p = new RegimeLabelParams(trendSmaDays: 200, trendHysteresisPct: 1.0, confirmDays: 5,
            volWindowDays: 21, volPercentile: 80, volLookbackSessions: 756);
        var closes = Enumerable.Range(0, 100).Select(i => 100.0 + i).ToList();
        Assert.Empty(RegimeLabeler.LabelSeries(Series(closes), p));
    }

    // ---- Fail closed on corrupt / out-of-order input (rule 10) ----
    [Fact]
    public void FR26_LabelSeries_RejectsNonPositiveClose()
    {
        var series = new List<ProxyClose> { new(D(0), 100), new(D(1), 0.0), new(D(2), 101) };
        Assert.Throws<ArgumentOutOfRangeException>(() => RegimeLabeler.TrendSeries(series, TrendParams()));
    }

    [Fact]
    public void FR26_LabelSeries_RejectsNonAscendingDates()
    {
        var series = new List<ProxyClose> { new(D(2), 100), new(D(1), 101), new(D(0), 102) };
        Assert.Throws<ArgumentException>(() => RegimeLabeler.TrendSeries(series, TrendParams()));
    }

    [Fact]
    public void FR26_Params_RejectNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegimeLabelParams(1, 1.0, 5, 21, 80, 756));   // sma < 2
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegimeLabelParams(200, -1.0, 5, 21, 80, 756)); // negative band
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegimeLabelParams(200, 1.0, 0, 21, 80, 756));  // confirm < 1
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegimeLabelParams(200, 1.0, 5, 21, 101, 756)); // pct > 100
    }
}
