using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Worker.Pipeline;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// Stage1Fetch in isolation: it gates the fetched series but drops flags on dates already gated on a
/// prior run (≤ LastStoredDate), so a re-fetch never re-emits yesterday's warnings — while a genuinely
/// new bar's flag is kept.
/// </summary>
public class Stage1FetchTests
{
    private static readonly string[] Dates =
        ["2024-01-01", "2024-01-02", "2024-01-03", "2024-01-04", "2024-01-05", "2024-01-06", "2024-01-07"];

    private static Stage1Fetch NewFetch(FakeMarketData market) =>
        new(market, new FakeRegimeProxy(), new DataQualityGate(new DataQualityOptions()));

    // A gentle ramp with one large upward spike at spikeIndex (a robust-z outlier).
    private static FakeMarketData Series(string symbol, int spikeIndex)
    {
        var market = new FakeMarketData();
        for (var i = 0; i < Dates.Length; i++)
        {
            var close = i == spikeIndex ? 300.0 : 100.0 + i * 0.5;
            market.SetBar(symbol, new EodBar(Dates[i], close, close, close, close, close, 5_000_000));
        }
        return market;
    }

    private static Stage1Request Request(string? lastStoredDate) => new(
        AsOf: Dates[^1],
        From: Dates[0],
        Watermark: $"{Dates[^1]}T22:00:00Z",
        ObservedAt: $"{Dates[^1]}T22:00:00Z",
        ExpectedDates: Dates,
        Securities: [new Stage1Target(1, "X", [], lastStoredDate)],
        Proxy: null);

    [Fact]
    public async Task NewDateOutlier_IsKept()
    {
        // Spike on the last (new) date; everything before it already gated.
        var staged = await NewFetch(Series("X", spikeIndex: Dates.Length - 1)).FetchAsync(Request(lastStoredDate: Dates[^2]));

        var flag = Assert.Single(staged.Securities[0].Report.Flags);
        Assert.Equal(QualityIssue.OutlierReturn, flag.Issue);
        Assert.Equal(Dates[^1], flag.Date);
        Assert.True(staged.FlagCount == 1);
    }

    [Fact]
    public async Task AlreadyGatedDateOutlier_IsDropped()
    {
        // Spike in the middle (old) — its flags are at/before LastStoredDate and must not re-emit.
        var staged = await NewFetch(Series("X", spikeIndex: 3)).FetchAsync(Request(lastStoredDate: Dates[5]));

        Assert.Empty(staged.Securities[0].Report.Flags);
        Assert.False(staged.HasRejects);
    }

    [Fact]
    public async Task NoPriorHistory_KeepsEveryFlag()
    {
        // LastStoredDate null (nothing gated before) ⇒ the historical spike's flags are all genuinely new.
        var staged = await NewFetch(Series("X", spikeIndex: 3)).FetchAsync(Request(lastStoredDate: null));

        Assert.NotEmpty(staged.Securities[0].Report.Flags);
        Assert.All(staged.Securities[0].Report.Flags, f => Assert.Equal(QualityIssue.OutlierReturn, f.Issue));
    }
}
