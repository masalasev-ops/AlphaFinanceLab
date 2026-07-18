using AlphaLab.Core.Domain;

namespace AlphaLab.Strategies.Tests;

/// <summary>The trivial trend-filter dummy: score 1.0 above the SMA, 0.0 below, omit thin history.
/// It exists to exercise the funnel with a strategy that actually selects a varying subset.</summary>
public class ThresholdModelTests
{
    private const string Wm = "2024-12-31T00:00:00Z";

    // Three names over 3 sessions ending at asOf=D2, lookback 3:
    //   A: 100,100,110 → SMA 103.33, close 110 > SMA ⇒ 1.0
    //   B: 110,105,100 → SMA 105,    close 100 < SMA ⇒ 0.0
    //   C: only 2 sessions ⇒ omitted (too little history)
    private static FakeMarket ThreeNames()
    {
        var m = new FakeMarket();
        void Bar(long id, string d, double c) => m.Add(id, d, c, c, c, 5_000_000);
        Bar(1, "2024-01-01", 100); Bar(1, "2024-01-02", 100); Bar(1, "2024-01-03", 110);
        Bar(2, "2024-01-01", 110); Bar(2, "2024-01-02", 105); Bar(2, "2024-01-03", 100);
        /* C only the last two */ Bar(3, "2024-01-02", 100); Bar(3, "2024-01-03", 105);
        return m;
    }

    [Fact]
    public async Task Score_AboveSma_IsOne_BelowSma_IsZero_ThinHistory_IsOmitted()
    {
        var model = ThresholdModel.Create(lookback: 3);
        var features = ThreeNames().At(new DateOnly(2024, 1, 3), Wm);
        var eligible = new[] { new SecurityId(1), new SecurityId(2), new SecurityId(3) };

        var scores = await model.ScoreUniverseAsync(eligible, new DateOnly(2024, 1, 3), features);

        Assert.Equal(1.0, scores[new SecurityId(1)]);
        Assert.Equal(0.0, scores[new SecurityId(2)]);   // 0.0 present but the zero-score invariant drops it in Stage 3
        Assert.DoesNotContain(new SecurityId(3), scores.Keys); // omitted — absence is the honest answer
    }

    [Fact]
    public async Task Score_IsDeterministic()
    {
        var model = ThresholdModel.Create(lookback: 3);
        var features = ThreeNames().At(new DateOnly(2024, 1, 3), Wm);
        var eligible = new[] { new SecurityId(1), new SecurityId(2), new SecurityId(3) };

        var a = await model.ScoreUniverseAsync(eligible, new DateOnly(2024, 1, 3), features);
        var b = await model.ScoreUniverseAsync(eligible, new DateOnly(2024, 1, 3), features);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Create_IsUnregistered_AndCarriesTheLookbackParam()
    {
        var model = ThresholdModel.Create(lookback: 50);
        Assert.Equal("threshold:sma50", model.Id);
        Assert.True(model.Config.Unregistered);                      // a dummy with no hypothesis, honestly flagged (rule 16)
        Assert.Equal(50, model.Config.Param(ThresholdModel.ParamLookback));
        Assert.IsType<ExitPolicy.ScheduledRebalance>(model.Exits);
        Assert.Equal(SizingMode.Equal, model.Config.Sizing);
    }

    [Fact]
    public void Create_RejectsATooShortLookback() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ThresholdModel.Create(lookback: 1));
}
