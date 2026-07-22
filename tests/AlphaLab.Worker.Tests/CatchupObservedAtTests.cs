using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// FX-CatchupObservedAt (TEST_PLAN §5; finding 194 / D92) — the Phase-4 prerequisite. A recovered day
/// must record the TRUE observation instant, never the session-derived {as_of}T22:00:00Z fiction:
/// replay reasons over observed_at, and a fabricated stamp would tell it a late recovery was seen the
/// session evening. The clock here is pinned at a deliberately odd instant (23:37:41Z) so the honest
/// stamp can never collide with any session-derived string.
/// </summary>
public class CatchupObservedAtTests
{
    // The harness's Run3 session (index 42 of the standard 50-session calendar) at an instant that is
    // past the ET close but unmistakably NOT a T22:00:00Z fiction.
    private static DateTimeOffset OddClock()
    {
        var run3 = DateOnly.ParseExact(PipelineHarness.SessionDate(42), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DateTimeOffset(run3.ToDateTime(new TimeOnly(23, 37, 41)), TimeSpan.Zero);
    }

    private static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    [Fact]
    public async Task FX_CatchupObservedAt_RecoveredDaysCarryTrueObservedAt()
    {
        var clock = OddClock();
        using var h = new PipelineHarness(clock);
        var honest = Iso(clock);

        var outcome = await h.RunCatchupAsync();
        Assert.False(outcome.StoppedEarly);
        Assert.Equal(3, outcome.Processed); // Run1, Run2 recovered; Run3 is the same-ET-day 'live' run

        using var db = h.Open();

        // The two RECOVERED days: run_kind='catchup', watermark = the true instant, never the fiction.
        foreach (var day in new[] { h.Run1, h.Run2 })
        {
            var run = Assert.Single(db.Runs.Where(r => r.AsOf == day && r.Status == "ok"));
            Assert.Equal("catchup", run.RunKind);
            Assert.Equal(honest, run.Watermark);
            Assert.NotEqual($"{day}T22:00:00Z", run.Watermark);

            // The day's freshly ingested bars carry the same honest observed_at — this is the row
            // replay actually reads (bars.observed_at), not just the run's header.
            var bar = Assert.Single(db.Bars.Where(b => b.SecurityId == PipelineHarness.MemberA && b.Date == day));
            Assert.Equal(honest, bar.ObservedAt);

            Assert.Single(db.CatchupLog.Where(c => c.AsOf == day));
        }

        // The same-evening LIVE day keeps the session-derived stamp (D92's bounded approximation):
        // the run happens the evening of the session, so {asOf}T22:00:00Z is an honest-enough clock
        // AND what keeps a same-day re-fetch a value-diff no-op.
        var live = Assert.Single(db.Runs.Where(r => r.AsOf == h.Run3 && r.Status == "ok"));
        Assert.Equal("live", live.RunKind);
        Assert.Equal($"{h.Run3}T22:00:00Z", live.Watermark);
        Assert.Empty(db.CatchupLog.Where(c => c.AsOf == h.Run3));
    }

    [Fact]
    public async Task FX_CatchupObservedAt_ReproduceDayStillByteIdentical()
    {
        var clock = OddClock();
        using var h = new PipelineHarness(clock);
        var honest = Iso(clock);

        var outcome = await h.RunCatchupAsync();
        Assert.False(outcome.StoppedEarly);

        // Reproduce Run2 — a RECOVERED day whose honest watermark no re-derivation could reconstruct.
        // The runner must pin the STORED watermark (D92): byte-identity here proves honest observed_at
        // and NFR-1 coexist.
        var runner = new ReproduceDayRunner(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Populations:Size"] = "6",
                ["Populations:CostFreeSize"] = "3",
            }).Build(),
            new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" },
            NullLoggerFactory.Instance);

        var reproduced = await runner.RunAsync($"Data Source={h.DbPath}", h.Run2);

        Assert.True(reproduced.Matches, string.Join("\n", reproduced.Differences));
        Assert.Equal(honest, reproduced.Watermark);
        Assert.NotEqual($"{h.Run2}T22:00:00Z", reproduced.Watermark);
    }
}
