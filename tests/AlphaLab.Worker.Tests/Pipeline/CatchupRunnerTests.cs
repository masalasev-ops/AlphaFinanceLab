using System.Globalization;
using AlphaLab.Data.Providers;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// FX-Outage5d + the FR-34 OnDemand catch-up behaviours (checkpoint 2.11): replay the gap in order, the
/// same-evening no-op, no run before the close, a multi-day outage recovered, and resumable stop on a
/// fail-closed day. Driven end-to-end through the D53 pipeline over the standard harness scenario (the
/// default clock puts the last completed session at Run3, so the natural gap is Run1..Run3).
/// </summary>
public class CatchupRunnerTests
{
    // ---- FR34_OnDemand_ReplaysGapThenExits: the gap is replayed oldest-first; today=live, earlier=catchup ----
    [Fact]
    public async Task Catchup_ReplaysTheGapInOrder_LabellingTodayLiveAndEarlierCatchup()
    {
        using var h = new PipelineHarness();

        var outcome = await h.RunCatchupAsync();

        Assert.Equal(3, outcome.MissedCount);   // Run1..Run3 (pre-seed boundary → last completed)
        Assert.Equal(3, outcome.Processed);
        Assert.False(outcome.StoppedEarly);

        using var db = h.Open();
        Assert.Equal(3, db.Runs.Count(r => r.Status == "ok"));
        Assert.Equal("catchup", db.Runs.Single(r => r.AsOf == h.Run1).RunKind); // recovered
        Assert.Equal("catchup", db.Runs.Single(r => r.AsOf == h.Run2).RunKind);
        Assert.Equal("live", db.Runs.Single(r => r.AsOf == h.Run3).RunKind);     // processed on its own ET day
        // catchup_log holds the recovered days only (not the live one).
        Assert.Equal(2, db.CatchupLog.Count());
        Assert.Contains(db.CatchupLog.ToList(), c => c.AsOf == h.Run1);
        Assert.DoesNotContain(db.CatchupLog.ToList(), c => c.AsOf == h.Run3);
        // The T+1 chain ran across the recovered days ⇒ fills happened.
        Assert.NotEmpty(db.Trades);
    }

    // ---- FR34_OnDemand_SameEveningNoop: a second launch the same evening finds nothing to do ----
    [Fact]
    public async Task Catchup_SameEveningReRun_IsANoop()
    {
        using var h = new PipelineHarness();

        await h.RunCatchupAsync();                 // processes Run1..Run3
        var second = await h.RunCatchupAsync();    // same clock ⇒ up to date

        Assert.Equal(0, second.MissedCount);
        Assert.Equal(0, second.Processed);
        using var db = h.Open();
        Assert.Equal(3, db.Runs.Count());          // no new run rows
    }

    // ---- FR34_OnDemand_NoRunBeforeClose: launching before today's close processes nothing new ----
    [Fact]
    public async Task Catchup_BeforeTodaysClose_ProcessesNothing()
    {
        // Clock = Run1's date at 12:00 UTC (07:00 ET) — before the 16:00 ET close, so the last completed
        // session is the pre-seed boundary (already covered by history) and there is no gap yet.
        using var h = new PipelineHarness(At(PipelineHarness.SessionDate(40), 12));

        var outcome = await h.RunCatchupAsync();

        Assert.Equal(0, outcome.MissedCount);
        using var db = h.Open();
        Assert.Empty(db.Runs);
    }

    // ---- FX-Outage5d: a five-session outage is recovered, one transaction per day ----
    [Fact]
    public async Task Catchup_FiveDayOutage_RecoversAllFive()
    {
        // Last completed = session 44 ⇒ the gap from the pre-seed boundary (session 40) is five days.
        using var h = new PipelineHarness(At(PipelineHarness.SessionDate(44), 22));

        var outcome = await h.RunCatchupAsync();

        Assert.Equal(5, outcome.MissedCount);
        Assert.Equal(5, outcome.Processed);
        Assert.False(outcome.StoppedEarly);
        using var db = h.Open();
        Assert.Equal(5, db.Runs.Count(r => r.Status == "ok"));
    }

    // ---- Resumable: a fail-closed day STOPS the loop; the committed prefix persists for the next launch ----
    [Fact]
    public async Task Catchup_FailClosedDay_StopsAndCommitsThePrefix()
    {
        using var h = new PipelineHarness();
        // A non-positive close on Run2 ⇒ the gate rejects that day (rule 10).
        h.Market.SetBar(PipelineHarness.MemberASymbol, new EodBar(h.Run2, 100, 100, 100, -5.0, -5.0, 10_000_000));

        var outcome = await h.RunCatchupAsync();

        Assert.True(outcome.StoppedEarly);
        Assert.Equal(1, outcome.Processed);       // Run1 committed, Run2 aborted → stop
        Assert.Equal(3, outcome.MissedCount);
        using var db = h.Open();
        Assert.Single(db.Runs.Where(r => r.Status == "ok").ToList());   // only Run1
        Assert.Equal(h.Run1, db.Runs.Single(r => r.Status == "ok").AsOf);
        Assert.Empty(db.Runs.Where(r => r.AsOf == h.Run2).ToList());    // Run2 wrote no row (aborted pre-open)
    }

    private static DateTimeOffset At(string isoDate, int utcHour) => new(
        DateOnly.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(new TimeOnly(utcHour, 0)),
        TimeSpan.Zero);
}
