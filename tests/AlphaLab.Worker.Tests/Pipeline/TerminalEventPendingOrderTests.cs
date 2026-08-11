using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// FX-TerminalEventPendingOrder (D143): a §13.6 event that ENDS a line on the same session a stored
/// order in that name was due to fill.
///
/// D142 restates an order when a corporate action re-denominates the position. A termination is the case
/// it deliberately reports NO factor for — there is no number that converts an order into a line that no
/// longer exists — so the remedy is cancellation, not conversion.
///
/// Both arms were defects and the quieter one was worse. The SELL arm rolled the whole day back; the BUY
/// arm silently created a position in a delisted security. Each fixture below asserts the corrected
/// behaviour AND names what it did before, because a test whose failure mode is unrecorded teaches the
/// next reader nothing.
/// </summary>
public class TerminalEventPendingOrderTests
{
    private const string Ew = "buyhold:ew";

    /// <summary>Insert a §13.6 terminal action directly. There is no delist/merger feed on the fake
    /// market provider — those kinds do not arrive through `GetSplitsAsync`/`GetDividendsAsync` — so the
    /// row goes in the way `CorporateActionApplierTests` plants one.</summary>
    private static void PlantTerminalAction(PipelineHarness h, long securityId, string type, string effectiveOn,
        decimal? cashPerShare = null, double? ratio = null, long? counterparty = null)
    {
        using var db = h.Open();
        db.CorporateActions.Add(new CorporateActionRow
        {
            SecurityId = securityId,
            Type = type,
            EffectiveDate = effectiveOn,
            Version = 1,
            CashPerShare = cashPerShare,
            Ratio = ratio,
            CounterpartySecurityId = counterparty,
            ObservedAt = $"{effectiveOn}T20:00:00Z",
            Source = "eodhd",
        });
        db.SaveChanges();
    }

    private static async Task<(PipelineHarness H, long AccountId, double Held)> OpenBookAsync()
    {
        var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        var accountId = h.AccountIdFor(Ew);
        using var db = h.Open();
        var held = db.Positions
            .Single(p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA).Shares;
        return (h, accountId, held);
    }

    [Fact]
    public async Task FR9_D143_ADelistOnADayWithAPendingSell_CancelsTheOrder_AndTheDayStillCommits()
    {
        var (h, accountId, held) = await OpenBookAsync();
        using var _ = h;

        PlantTerminalAction(h, PipelineHarness.MemberA, "delist", h.Run3);
        h.PlantPriorOrder(accountId, Ew, h.Run2, new PlannedOrder
        {
            SecurityId = new SecurityId(PipelineHarness.MemberA),
            Side = TradeSide.Sell,
            Shares = held,
            Reason = TradeReason.ExitPolicy,
            DecidedOn = h.Run2,
            FillOn = h.Run3,
            Rationale = "planted by FX-TerminalEventPendingOrder",
        });

        // PRE-D143 this THREW at PostFill's `existing is null` guard and Stage 2 rolled the entire day
        // back — for an ordinary corporate event, with a message blaming the funnel.
        var result = await h.RunAsync(h.Run3);
        Assert.True(result.Committed);

        using var db = h.Open();

        // The §13.6 force-exit is the disposal, and it is the ONLY sale.
        var trades = db.Trades
            .Where(t => t.AccountId == accountId && t.FilledOn == h.Run3 && t.SecurityId == PipelineHarness.MemberA)
            .ToList();
        var forced = Assert.Single(trades);
        Assert.Equal("corp_action", forced.Reason);
        Assert.NotNull(forced.ActionId);
        Assert.DoesNotContain(trades, t => t.Reason == "exit_policy");

        // The line is gone, and no ghost row survived the cancellation.
        Assert.DoesNotContain(db.Positions.ToList(),
            p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA);
    }

    [Fact]
    public async Task FR9_D143_APendingBuyIntoATerminatedName_DoesNotFabricateAPosition()
    {
        // THE SILENT ARM, and the reason this jumped the queue. PostFill's buy leg has no existence
        // check — legitimately, since buying into an unheld name is how a position opens — so before
        // D143 this fill CREATED a position in a delisted security. It would then freeze forever on the
        // next session's stoppage check and be indistinguishable from a real holding.
        var (h, accountId, _) = await OpenBookAsync();
        using var _u = h;

        PlantTerminalAction(h, PipelineHarness.MemberB, "delist", h.Run3);
        h.PlantPriorOrder(accountId, Ew, h.Run2, new PlannedOrder
        {
            SecurityId = new SecurityId(PipelineHarness.MemberB),
            Side = TradeSide.Buy,
            Shares = 5.0,
            Reason = TradeReason.Wishlist,
            DecidedOn = h.Run2,
            FillOn = h.Run3,
            Rationale = "planted by FX-TerminalEventPendingOrder (buy arm)",
        });

        var result = await h.RunAsync(h.Run3);
        Assert.True(result.Committed);

        using var db = h.Open();
        Assert.DoesNotContain(db.Trades.ToList(),
            t => t.AccountId == accountId && t.FilledOn == h.Run3
                 && t.SecurityId == PipelineHarness.MemberB && t.Reason == "wishlist");
        Assert.DoesNotContain(db.Positions.ToList(),
            p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberB);
    }

    [Fact]
    public async Task FR9_D143_ACashMergerOnADayWithAPendingSell_CancelsTheOrder()
    {
        // The merger arm, which only ever failed ACCIDENTALLY safe: a merged-out target usually has no
        // bar, so the broker rejected on a missing price. The harness gives it a bar, so pre-D143 this
        // reached the same throw the delist did — proving the safety was the absence of data, not a rule.
        var (h, accountId, held) = await OpenBookAsync();
        using var _ = h;

        PlantTerminalAction(h, PipelineHarness.MemberA, "merger_cash", h.Run3, cashPerShare: 54.20m);
        h.PlantPriorOrder(accountId, Ew, h.Run2, new PlannedOrder
        {
            SecurityId = new SecurityId(PipelineHarness.MemberA),
            Side = TradeSide.Sell,
            Shares = held,
            Reason = TradeReason.ExitPolicy,
            DecidedOn = h.Run2,
            FillOn = h.Run3,
            Rationale = "planted by FX-TerminalEventPendingOrder (merger)",
        });

        var result = await h.RunAsync(h.Run3);
        Assert.True(result.Committed);

        using var db = h.Open();
        var forced = Assert.Single(db.Trades.Where(t =>
            t.AccountId == accountId && t.FilledOn == h.Run3 && t.SecurityId == PipelineHarness.MemberA));
        Assert.Equal("corp_action", forced.Reason);
        Assert.Equal(54.20m, forced.RawFillPrice);
    }

    [Fact]
    public async Task FR9_D143_ANonTerminalActionOnTheSameDay_DoesNotCancelTheOrder()
    {
        // The other side of the boundary. A DIVIDEND on the fill date leaves the line perfectly tradable,
        // and cancelling there would silently drop a valid order — the mirror defect of filling into a
        // closed one. This is what fails if the terminated set is ever widened to "any action today".
        var (h, accountId, held) = await OpenBookAsync();
        using var _ = h;

        h.Market.AddDividend(PipelineHarness.MemberASymbol,
            new AlphaLab.Data.Providers.DividendEvent(h.Run3, 1.50m, 1.50m));
        h.PlantPriorOrder(accountId, Ew, h.Run2, new PlannedOrder
        {
            SecurityId = new SecurityId(PipelineHarness.MemberA),
            Side = TradeSide.Sell,
            Shares = held,
            Reason = TradeReason.ExitPolicy,
            DecidedOn = h.Run2,
            FillOn = h.Run3,
            Rationale = "planted by FX-TerminalEventPendingOrder (non-terminal)",
        });

        var result = await h.RunAsync(h.Run3);
        Assert.True(result.Committed);

        using var db = h.Open();
        var sale = Assert.Single(db.Trades.Where(t =>
            t.AccountId == accountId && t.FilledOn == h.Run3
            && t.SecurityId == PipelineHarness.MemberA && t.Reason == "exit_policy"));
        Assert.Equal(held, sale.Shares, 9);
        Assert.DoesNotContain(db.Positions.ToList(),
            p => p.AccountId == accountId && p.SecurityId == PipelineHarness.MemberA);
    }
}
