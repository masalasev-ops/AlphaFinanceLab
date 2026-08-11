using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// FX-SplitPendingOrder (D142): a corporate action effective on a stored order's FILL date.
///
/// The pipeline applies corporate actions before it fills the prior session's orders (D53 ordering), so
/// between the decision at close T and the fill at open T+1 the book can be restated into different share
/// units while the stored order is still denominated in the old ones.
///
/// THESE FIXTURES EXIST IN TWO STATES, ON PURPOSE. At this commit the oversell guard is in and the
/// restatement is not, so the reverse-split case ROLLS THE DAY BACK — that is the guard being SEEN to
/// fire on the geometry it was written for, which is the only way to know a backstop works. The next
/// commit adds the restatement and this file's assertions move to the corrected fills. The diff between
/// the two is the evidence; a guard that was never observed to go red is a guard nobody has tested.
/// </summary>
public class SplitPendingOrderTests
{
    private const string Ew = "buyhold:ew";

    /// <summary>Run two sessions so the equal-weight book is open and priced, then report what it holds
    /// in MEMBERA at the close of Run2 — the quantity a stored order would have been built against.</summary>
    private static async Task<(long AccountId, double Held)> OpenBookAsync(PipelineHarness h)
    {
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        var accountId = h.AccountIdFor(Ew);
        using var db = h.Open();
        var held = db.Positions
            .Single(p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA).Shares;
        Assert.True(held > 0, "the harness must hold MEMBERA after two sessions for this fixture to mean anything.");
        return (accountId, held);
    }

    private static PlannedOrder Sell(double shares, string decidedOn, string fillOn) => new()
    {
        SecurityId = new SecurityId(PipelineHarness.MemberA),
        Side = TradeSide.Sell,
        Shares = shares,
        Reason = TradeReason.ExitPolicy,
        DecidedOn = decidedOn,
        FillOn = fillOn,
        Rationale = "planted by FX-SplitPendingOrder",
    };

    [Fact]
    public async Task FR9_D142_AnOversellNoCorporateActionExplains_RollsTheWholeDayBack()
    {
        // THE GUARD, REACHED THROUGH THE PIPELINE, with no corporate action anywhere near it. This is the
        // fixture that keeps proving the backstop fires once the restatement has made the split path
        // unable to reach it.
        using var h = new PipelineHarness();
        var (accountId, held) = await OpenBookAsync(h);

        h.PlantPriorOrder(accountId, Ew, h.Run2, Sell(held * 2.0, h.Run2, h.Run3));

        await Assert.ThrowsAnyAsync<Exception>(() => h.RunAsync(h.Run3));

        using var db = h.Open();

        // Stage 2 rolled back: no committed run for the day, and the book is exactly as it was.
        Assert.DoesNotContain(db.Runs.ToList(), r => r.AsOf == h.Run3 && r.Status == "ok");
        Assert.DoesNotContain(db.Trades.ToList(), t => t.FilledOn == h.Run3 && t.AccountId == accountId);
        Assert.Equal(held, db.Positions
            .Single(p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA).Shares, 9);

        // BEFORE the guard this test would have COMMITTED: the line deleted as a clean close and the full
        // double-size proceeds credited, with no exception and no log line. The assertion above is the one
        // that would have gone green on fabricated cash.
    }

    [Fact]
    public async Task FR9_D142_FxSplitPendingOrder_AReverseSplit_RollsTheDayBack_UntilTheOrderIsRestated()
    {
        // A 1-for-2 reverse split effective on the fill date: the book halves to held/2 while the stored
        // sell is still for `held`. Without the restatement that is an oversell, and the guard stops the
        // day rather than absorbing it. THIS ASSERTION IS REPLACED by the corrected-fill assertions in the
        // commit that adds the restatement; it is committed in this state so the red is a recorded fact.
        using var h = new PipelineHarness();
        var (accountId, held) = await OpenBookAsync(h);

        h.Market.AddSplit(PipelineHarness.MemberASymbol, new SplitEvent(h.Run3, 0.5, "1/2"));
        h.RescaleBarsFrom(PipelineHarness.MemberASymbol, h.Run3, 2.0);
        h.PlantPriorOrder(accountId, Ew, h.Run2, Sell(held, h.Run2, h.Run3));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.RunAsync(h.Run3));

        Assert.Contains("OVERSELL", ex.ToString(), StringComparison.Ordinal);

        using var db = h.Open();
        Assert.DoesNotContain(db.Runs.ToList(), r => r.AsOf == h.Run3 && r.Status == "ok");
    }

    [Fact]
    public async Task FR9_D142_FxSplitPendingOrder_AForwardSplit_LeavesTheLinePartlyOpen_UntilTheOrderIsRestated()
    {
        // The other direction, and the reason the severity moved critical → high. A 2-for-1 GROWS the book
        // to held×2, so the stale sell of `held` can never oversell — the day commits, equity is
        // arithmetically correct, and the defect is silent: an ExitPolicy CLOSE left half the line open.
        // No guard can catch this one, which is why the restatement is the fix and the guard is only the
        // backstop. THIS ASSERTION IS ALSO REPLACED in the restatement commit.
        using var h = new PipelineHarness();
        var (accountId, held) = await OpenBookAsync(h);

        h.Market.AddSplit(PipelineHarness.MemberASymbol, new SplitEvent(h.Run3, 2.0, "2/1"));
        h.RescaleBarsFrom(PipelineHarness.MemberASymbol, h.Run3, 0.5);
        h.PlantPriorOrder(accountId, Ew, h.Run2, Sell(held, h.Run2, h.Run3));

        var result = await h.RunAsync(h.Run3);
        Assert.True(result.Committed);

        using var db = h.Open();
        var remaining = db.Positions
            .SingleOrDefault(p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA);

        // A close that did not close. The restated book was held×2; the stale order sold `held`.
        Assert.NotNull(remaining);
        Assert.Equal(held, remaining!.Shares, 6);
    }

    [Fact]
    public async Task FR9_D142_FxSplitPendingOrder_NoSplit_FillsTheStoredOrderVerbatim()
    {
        // The no-op arm, permanent in both states: with nothing restated the stored order must fill for
        // exactly what it says, and the line must close. This is what fails if a restatement ever fires
        // on a day that had no corporate action.
        using var h = new PipelineHarness();
        var (accountId, held) = await OpenBookAsync(h);

        h.PlantPriorOrder(accountId, Ew, h.Run2, Sell(held, h.Run2, h.Run3));

        var result = await h.RunAsync(h.Run3);
        Assert.True(result.Committed);

        using var db = h.Open();
        var trade = db.Trades.Single(t =>
            t.AccountId == accountId && t.FilledOn == h.Run3 && t.SecurityId == PipelineHarness.MemberA);

        Assert.Equal(held, trade.Shares, 9);
        Assert.DoesNotContain(db.Positions.ToList(),
            p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA);
    }
}
