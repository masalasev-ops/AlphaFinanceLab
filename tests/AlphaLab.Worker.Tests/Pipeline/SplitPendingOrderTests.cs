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
/// units while the stored order is still written in the old ones. D142 restates the order by the same
/// factor; the oversell guard is the backstop for when that pairing is broken.
///
/// THE ASSERTION STYLE IS DELIBERATE. The split cases are not checked against a hand-computed share count
/// — that would only restate the implementation's own arithmetic back at it. They are checked against a
/// RUN OF THE SAME SCENARIO WITH NO SPLIT: a split re-denominates a holding and re-denominates the quote
/// in the same breath, so an ExitPolicy close must realise the SAME VALUE either way. That is §13.6's
/// "equity unchanged" applied to the fill, and it is a claim about the world rather than about the code.
///
/// (These fixtures were committed one commit earlier asserting the PRE-restatement behaviour, with the
/// reverse-split case rolling the day back on the oversell guard. The diff between that state and this
/// one is the red→green evidence; the guard's own fixture below stays behind permanently.)
/// </summary>
public class SplitPendingOrderTests
{
    private const string Ew = "buyhold:ew";

    private sealed record CloseOutcome(
        bool Committed, double Held, double TradeShares, decimal Proceeds, double? LeftoverShares);

    /// <summary>
    /// Run two sessions so the equal-weight book is open and priced, plant an ExitPolicy close of the
    /// whole line for Run3, optionally plant a split effective on Run3, and report what the fill did.
    /// </summary>
    private static async Task<CloseOutcome> RunCloseAsync(double? splitRatio, double priceFactor = 1.0)
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        var accountId = h.AccountIdFor(Ew);
        double held;
        using (var db = h.Open())
        {
            held = db.Positions
                .Single(p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA).Shares;
        }
        Assert.True(held > 0, "the harness must hold MEMBERA after two sessions for this fixture to mean anything.");

        if (splitRatio is { } ratio)
        {
            h.Market.AddSplit(PipelineHarness.MemberASymbol, new SplitEvent(h.Run3, ratio, $"{ratio}/1"));
            h.RescaleBarsFrom(PipelineHarness.MemberASymbol, h.Run3, priceFactor);
        }

        h.PlantPriorOrder(accountId, Ew, h.Run2, new PlannedOrder
        {
            SecurityId = new SecurityId(PipelineHarness.MemberA),
            Side = TradeSide.Sell,
            Shares = held,
            Reason = TradeReason.ExitPolicy,
            DecidedOn = h.Run2,
            FillOn = h.Run3,
            Rationale = "planted by FX-SplitPendingOrder",
        });

        var result = await h.RunAsync(h.Run3);

        using var read = h.Open();
        var trade = read.Trades.Single(t =>
            t.AccountId == accountId && t.FilledOn == h.Run3 && t.SecurityId == PipelineHarness.MemberA);
        var leftover = read.Positions
            .Where(p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA)
            .Select(p => (double?)p.Shares)
            .FirstOrDefault();

        return new CloseOutcome(
            result.Committed, held, trade.Shares, trade.Shares.ToDecimal() * trade.RawFillPrice, leftover);
    }

    [Fact]
    public async Task FR9_D142_FxSplitPendingOrder_AReverseSplit_RestatesTheStaleSell_NoOversell()
    {
        // 1-for-2 on the fill date: the book halves and the quote doubles. Pre-D142 the stored sell of
        // `held` exceeded the restated book and the oversell guard rolled the day back; before the guard
        // it committed, deleted the line and credited ~2× its true value.
        var baseline = await RunCloseAsync(splitRatio: null);
        var split = await RunCloseAsync(splitRatio: 0.5, priceFactor: 2.0);

        Assert.True(split.Committed);
        Assert.Equal(baseline.Held, split.Held, 9);                     // same scenario up to the split
        Assert.Equal(baseline.Held * 0.5, split.TradeShares, 9);        // sold in the book's units
        Assert.Equal(baseline.Proceeds, split.Proceeds, 6);             // THE INVARIANT: same value realised
        Assert.Null(split.LeftoverShares);                              // and the line actually closed
    }

    [Fact]
    public async Task FR9_D142_FxSplitPendingOrder_AForwardSplit_ClosesTheWholeRestatedLine()
    {
        // 2-for-1 GROWS the book, so no guard could ever have caught this one: pre-D142 the day committed
        // with equity arithmetically correct and an ExitPolicy CLOSE silently leaving half the line open.
        // That is why the restatement is the fix and the guard is only the backstop.
        var baseline = await RunCloseAsync(splitRatio: null);
        var split = await RunCloseAsync(splitRatio: 2.0, priceFactor: 0.5);

        Assert.True(split.Committed);
        Assert.Equal(baseline.Held * 2.0, split.TradeShares, 9);
        Assert.Equal(baseline.Proceeds, split.Proceeds, 6);
        Assert.Null(split.LeftoverShares);      // pre-D142 this held `baseline.Held` — a close that did not close
    }

    [Fact]
    public async Task FR9_D142_FxSplitPendingOrder_NoSplit_FillsTheStoredOrderVerbatim()
    {
        // The no-op arm. OrderRestatement returns the SAME INSTANCE when nothing restated the security,
        // so this is what fails if a restatement ever fires on a day that had no corporate action.
        var outcome = await RunCloseAsync(splitRatio: null);

        Assert.True(outcome.Committed);
        Assert.Equal(outcome.Held, outcome.TradeShares, 9);
        Assert.Null(outcome.LeftoverShares);
    }

    [Fact]
    public async Task FR9_D142_ASplitOnAnUnheldName_StillRestatesThePendingBuy()
    {
        // THE BUY-SIDE HOLE, and the fixture that fails if the ratio map is derived from the account's
        // BOOK. The applier iterates HELD securities, so a pending buy into a name the account does not
        // hold is invisible to it: the fill would buy the ordered count at the restated price and spend
        // 1/r of the intended notional, silently, in breach of D84's cash sizing at the fill.
        //
        // The step-0 probe found this shape in the frozen generation (threshold:sma50, 2010-06-08, a
        // 21.4 sh open into an unheld name on a 2-for-1 date), so it is not hypothetical.
        var baseline = await RunUnheldBuyAsync(splitRatio: null);
        var split = await RunUnheldBuyAsync(splitRatio: 0.5, priceFactor: 2.0);

        Assert.Equal(baseline.Shares * 0.5, split.Shares, 9);
        Assert.Equal(baseline.Spent, split.Spent, 6);   // the intended notional, not 2× it
    }

    private sealed record BuyOutcome(double Shares, decimal Spent);

    /// <summary>Plant a BUY into the cap-weight proxy on the equal-weight account, which never holds it —
    /// so the security appears in the day's ORDERS but not in this account's book.</summary>
    private static async Task<BuyOutcome> RunUnheldBuyAsync(double? splitRatio, double priceFactor = 1.0)
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        var accountId = h.AccountIdFor(Ew);
        using (var db = h.Open())
        {
            Assert.DoesNotContain(db.Positions.ToList(),
                p => p.AccountId == accountId && p.SecurityId == PipelineHarness.CwProxy);
        }

        if (splitRatio is { } ratio)
        {
            h.Market.AddSplit(PipelineHarness.CwSymbol, new SplitEvent(h.Run3, ratio, $"{ratio}/1"));
            h.RescaleBarsFrom(PipelineHarness.CwSymbol, h.Run3, priceFactor);
        }

        h.PlantPriorOrder(accountId, Ew, h.Run2, new PlannedOrder
        {
            SecurityId = new SecurityId(PipelineHarness.CwProxy),
            Side = TradeSide.Buy,
            Shares = 10.0,
            Reason = TradeReason.Wishlist,
            DecidedOn = h.Run2,
            FillOn = h.Run3,
            Rationale = "planted by FX-SplitPendingOrder (unheld name)",
        });

        var result = await h.RunAsync(h.Run3);
        Assert.True(result.Committed);

        using var read = h.Open();
        var trade = read.Trades.Single(t =>
            t.AccountId == accountId && t.FilledOn == h.Run3 && t.SecurityId == PipelineHarness.CwProxy);

        return new BuyOutcome(trade.Shares, trade.Shares.ToDecimal() * trade.RawFillPrice);
    }

    [Fact]
    public async Task FR9_D142_AnOversellNoCorporateActionExplains_RollsTheWholeDayBack()
    {
        // THE GUARD, REACHED THROUGH THE PIPELINE, with no corporate action anywhere near it. This is the
        // fixture that keeps proving the backstop fires now that the restatement has made the split path
        // unable to reach it — a guard nobody can still see go red is a guard nobody is testing.
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        var accountId = h.AccountIdFor(Ew);
        double held;
        using (var db = h.Open())
        {
            held = db.Positions
                .Single(p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA).Shares;
        }

        h.PlantPriorOrder(accountId, Ew, h.Run2, new PlannedOrder
        {
            SecurityId = new SecurityId(PipelineHarness.MemberA),
            Side = TradeSide.Sell,
            Shares = held * 2.0,
            Reason = TradeReason.ExitPolicy,
            DecidedOn = h.Run2,
            FillOn = h.Run3,
            Rationale = "planted by FX-SplitPendingOrder (unexplained oversell)",
        });

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.RunAsync(h.Run3));
        Assert.Contains("OVERSELL", ex.ToString(), StringComparison.Ordinal);

        using var db2 = h.Open();
        Assert.DoesNotContain(db2.Runs.ToList(), r => r.AsOf == h.Run3 && r.Status == "ok");
        Assert.DoesNotContain(db2.Trades.ToList(), t => t.FilledOn == h.Run3 && t.AccountId == accountId);
        Assert.Equal(held, db2.Positions
            .Single(p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA).Shares, 9);
    }
}

internal static class SharesDecimalExtensions
{
    /// <summary>Share counts are REAL and money is decimal (D69); this is the one place the fixtures
    /// cross that boundary, kept explicit rather than scattered through casts.</summary>
    public static decimal ToDecimal(this double shares) => (decimal)shares;
}
