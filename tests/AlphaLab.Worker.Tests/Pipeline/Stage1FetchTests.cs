using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Worker.Pipeline;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// Stage1Fetch in isolation: it gates the fetched series but drops flags on dates already gated on a
/// prior run (≤ LastStoredDate), so a re-fetch never re-emits yesterday's warnings — while a genuinely
/// new bar's flag is kept.
///
/// D145 makes that filter SEVERITY-AWARE. A warning on an already-gated date is still dropped (P7: the
/// 40-session context tail is re-gated daily and re-emitting its warnings is spam). A REJECT survives —
/// but only when the fetched bar actually DIFFERS from the stored one, because a reject on an unchanged
/// stored bar would fire again on every run forever and the arena would never commit another day.
/// </summary>
public class Stage1FetchTests
{
    private static readonly string[] Dates =
        ["2024-01-01", "2024-01-02", "2024-01-03", "2024-01-04", "2024-01-05", "2024-01-06", "2024-01-07"];

    private static Stage1Fetch NewFetch(FakeMarketData market) =>
        new(market, new FakeRegimeProxy(), new DataQualityGate(new DataQualityOptions()));

    // A gentle ramp with one large upward spike at spikeIndex (a robust-z outlier ⇒ WARN, not a reject:
    // 300 against ~101 is 3×, well inside MaxSingleDayPriceFactor's 10×).
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

    /// <summary>A ramp with a NON-POSITIVE close at <paramref name="badIndex"/> — a fail-closed REJECT
    /// (`CheckFieldIntegrity`), which is what a corrupt vendor value looks like.</summary>
    private static FakeMarketData SeriesWithRejectAt(string symbol, int badIndex)
    {
        var market = new FakeMarketData();
        for (var i = 0; i < Dates.Length; i++)
        {
            var close = i == badIndex ? -5.0 : 100.0 + i * 0.5;
            market.SetBar(symbol, new EodBar(Dates[i], 100.0, 100.0, 100.0, close, close, 5_000_000));
        }
        return market;
    }

    private static BarRow Stored(string date, double close, double open = 100.0) => new()
    {
        SecurityId = 1, Date = date, Version = 1, ObservedAt = $"{date}T22:00:00Z",
        Open = open, High = 100.0, Low = 100.0, Close = close, AdjClose = close,
        Volume = 5_000_000, Source = "eodhd",
    };

    private static Stage1Request Request(string? lastStoredDate, IReadOnlyList<BarRow>? storedBars = null) => new(
        AsOf: Dates[^1],
        From: Dates[0],
        Watermark: $"{Dates[^1]}T22:00:00Z",
        ObservedAt: $"{Dates[^1]}T22:00:00Z",
        ExpectedDates: Dates,
        Securities: [new Stage1Target(1, "X", [], lastStoredDate, storedBars ?? [])],
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

    [Fact]
    public async Task D145_ARejectOnAnAlreadyGatedDate_SurvivesWhenTheFetchedBarIsACorrection()
    {
        // THE DEFECT. The vendor now serves a corrupt value for a date already stored, and it DIFFERS from
        // what is stored — so ingestion would append it as a new bar version, which rule 3 then makes
        // permanent. Pre-D145 the date filter dropped the reject, the day committed, and the corrupt
        // correction went in with no abort and no flag row.
        var stored = Dates.Select(d => Stored(d, close: 100.0)).ToList();

        var staged = await NewFetch(SeriesWithRejectAt("X", badIndex: 3))
            .FetchAsync(Request(lastStoredDate: Dates[5], storedBars: stored));

        Assert.True(staged.HasRejects);
        var reject = Assert.Single(staged.Securities[0].Report.Flags, f => f.Severity == QualitySeverity.Reject);
        Assert.Equal(Dates[3], reject.Date);
    }

    [Fact]
    public async Task D145_ARejectOnAnUNCHANGEDStoredBar_IsStillDropped_SoTheArenaCannotWedge()
    {
        // THE REASON THE RULE IS NOT "KEEP EVERY REJECT". P7's open backlog is ~488k backfilled bars the
        // gate never vetted, so a STORED bar can itself be gate-rejectable. If an unchanged re-fetch of one
        // aborted the day, it would abort every day forever and the arena would never commit again. An
        // identical re-fetch is an idempotent no-op — there is nothing new to refuse.
        var stored = Dates
            .Select((d, i) => Stored(d, close: i == 3 ? -5.0 : 100.0))
            .ToList();

        var staged = await NewFetch(SeriesWithRejectAt("X", badIndex: 3))
            .FetchAsync(Request(lastStoredDate: Dates[5], storedBars: stored));

        Assert.False(staged.HasRejects);
        Assert.Empty(staged.Securities[0].Report.Flags);
    }

    [Fact]
    public async Task D145_AWarningOnAChangedStoredBar_IsStillDropped_BecauseP7IsAboutWarnings()
    {
        // The severity discrimination, from the other side. The bar CHANGED, so the correction test would
        // pass — but the flag is only a WARN, and P7's rationale (no duplicate warnings across the daily
        // re-gate of the 40-session context tail) is untouched by D145. This is what fails if someone
        // "simplifies" the predicate to keep any flag on a changed bar.
        var stored = Dates.Select(d => Stored(d, close: 100.0)).ToList();

        var staged = await NewFetch(Series("X", spikeIndex: 3))
            .FetchAsync(Request(lastStoredDate: Dates[5], storedBars: stored));

        Assert.Empty(staged.Securities[0].Report.Flags);
        Assert.False(staged.HasRejects);
    }

    [Fact]
    public async Task D145_ARejectOnADateWithNoStoredBarAtAll_Survives()
    {
        // A gap being filled below LastStoredDate: there is no stored row to compare against, so the
        // incoming bar is new data and its reject stands. Refusing here is the fail-closed direction — the
        // alternative silently ingests a corrupt bar into a hole.
        var stored = Dates.Where((_, i) => i != 3).Select(d => Stored(d, close: 100.0)).ToList();

        var staged = await NewFetch(SeriesWithRejectAt("X", badIndex: 3))
            .FetchAsync(Request(lastStoredDate: Dates[5], storedBars: stored));

        Assert.True(staged.HasRejects);
    }
}
