using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// The §13.6 freeze rail through Stage 4 (D147).
///
/// A frozen position is unpriceable — an unmapped corporate action or a bar stoppage pinned its
/// valuation — so it is held untouched until an operator resolves the freeze through D55's audited
/// action. "Untouched" has to mean not sized and not traded, not merely not closed.
///
/// THERE WERE NO PORTFOLIOPLANNER TESTS AT ALL BEFORE THIS FILE, which is the honest explanation for how
/// the defect survived: the planner put a frozen name into <c>Holds</c> beneath a comment promising it
/// was held untouched, and <c>ToSize</c> unions Holds with Opens on a whole-book rebalance. Nothing
/// asserted either half, so the comment and the behaviour drifted apart with no failing test between them.
/// </summary>
public class PortfolioPlannerFreezeTests
{
    private static readonly SecurityId Frozen = new(1);
    private static readonly SecurityId Tradable = new(2);

    private static Position At(SecurityId id, bool frozen = false) => new()
    {
        AccountId = 7,
        SecurityId = id,
        Shares = 100,
        CostBasis = 10_000m,
        OpenedOn = "2026-01-02",
        Frozen = frozen,
        FrozenReason = frozen ? "bar stoppage with no corporate action to explain it" : null,
    };

    /// <summary>A rebalance day: `SessionsSinceInception` is a multiple of the cadence.</summary>
    private static ExitContext RebalanceDay(params SecurityId[] wishList) => new()
    {
        AsOf = new DateOnly(2026, 3, 2),
        Ranks = new Dictionary<SecurityId, int> { [Frozen] = 1, [Tradable] = 2 },
        WishList = wishList.ToHashSet(),
        SessionsSinceInception = 21,
    };

    [Fact]
    public void FR9_D147_OnAWholeBookRebalance_AFrozenNameIsNotSized()
    {
        // THE DEFECT, at the seam that produced it. Pre-D147 the frozen name was in Holds, ToSize unioned
        // Holds with Opens on a WholeBook day, and Stage 5 re-sized it — pricing a fill at the very number
        // the freeze had declared untrustworthy, on every rebalance day, indefinitely.
        var plan = PortfolioPlanner.Plan(
            [At(Frozen, frozen: true), At(Tradable)],
            new ExitPolicy.ScheduledRebalance(21),
            RebalanceDay(Frozen, Tradable));

        Assert.Equal(RebalanceScope.WholeBook, plan.Scope);
        Assert.DoesNotContain(Frozen, plan.ToSize);
        Assert.Contains(Tradable, plan.ToSize);

        // It is not lost, either: it is held, in its own list, with its reason on the record.
        Assert.Contains(Frozen, plan.Frozen);
        Assert.DoesNotContain(Frozen, plan.Holds);
        Assert.Contains(plan.Notes, n => n.Id == Frozen && n.Reason.Contains("frozen", StringComparison.Ordinal));
    }

    [Fact]
    public void FR9_D147_AFrozenNameIsNeverClosed_TheExitPolicyIsNotConsulted()
    {
        // The other half of "untouched": a freeze suspends ACTION, not just sizing. Even with the name off
        // today's wish list on a rebalance day — the shape that closes a tradable name — it is not closed.
        var plan = PortfolioPlanner.Plan(
            [At(Frozen, frozen: true)],
            new ExitPolicy.ScheduledRebalance(21),
            RebalanceDay(/* wish list: empty */));

        Assert.Empty(plan.Closes);
        Assert.Contains(Frozen, plan.Frozen);
        Assert.Empty(plan.ToSize);
    }

    [Fact]
    public void FR9_D147_OnAnOpensOnlyDay_AFrozenNameWasNeverSizedAnyway_AndStillIsNot()
    {
        // The pre-existing correct behaviour, pinned so the fix is shown to have changed only the
        // rebalance path. OpensOnly sizes Opens alone, so the frozen name was safe on ordinary days —
        // which is precisely why the defect was invisible until a 21st session came round.
        var plan = PortfolioPlanner.Plan(
            [At(Frozen, frozen: true)],
            new ExitPolicy.Never(),
            RebalanceDay(Frozen));

        Assert.Equal(RebalanceScope.OpensOnly, plan.Scope);
        Assert.DoesNotContain(Frozen, plan.ToSize);
        Assert.Contains(Frozen, plan.Frozen);
    }

    [Fact]
    public void FR9_D147_ATradableNameOnARebalanceDay_IsStillSized()
    {
        // The anti-vacuity check: the fix must not have made ToSize empty for everyone. Without this, a
        // planner that returned nothing would satisfy every assertion above.
        var plan = PortfolioPlanner.Plan(
            [At(Tradable)],
            new ExitPolicy.ScheduledRebalance(21),
            RebalanceDay(Tradable));

        Assert.Equal(RebalanceScope.WholeBook, plan.Scope);
        Assert.Contains(Tradable, plan.ToSize);
        Assert.Empty(plan.Frozen);
    }
}
