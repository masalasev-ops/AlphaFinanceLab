using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;

namespace AlphaLab.Core.Tests;

/// <summary>
/// The ExitPolicy executor — the ONLY thing that may close a position on signal (hard rule 7).
/// These pin the three executable shapes, the two that must refuse, and the rule-7 boundary that
/// makes "fell off the wish list" never a sell.
/// </summary>
public class ExitPolicyExecutorTests
{
    private static SecurityId S(long id) => new(id);
    private static readonly DateOnly AsOf = new(2026, 7, 16);

    private static ExitContext Ctx(
        IReadOnlyDictionary<SecurityId, int>? ranks = null,
        IReadOnlySet<SecurityId>? wish = null,
        int sessions = 5) => new()
        {
            AsOf = AsOf,
            Ranks = ranks ?? new Dictionary<SecurityId, int>(),
            WishList = wish ?? new HashSet<SecurityId>(),
            SessionsSinceInception = sessions,
        };

    // ============================ Never ============================

    [Fact]
    public void FR9_Never_AlwaysHolds_OnlyAForcedEventCanClose()
    {
        var verdict = ExitPolicyExecutor.Evaluate(new ExitPolicy.Never(), S(1), Ctx());

        var hold = Assert.IsType<ExitVerdict.Hold>(verdict);
        Assert.Contains("Never", hold.Reason);
    }

    // ============================ RankBuffer ============================

    [Fact]
    public void FR9_RankBuffer_HoldsWithinTheBuffer_ClosesPastIt()
    {
        var policy = new ExitPolicy.RankBuffer(ExitRank: 80);

        var inside = ExitPolicyExecutor.Evaluate(policy, S(1), Ctx(new Dictionary<SecurityId, int> { [S(1)] = 80 }));
        var outside = ExitPolicyExecutor.Evaluate(policy, S(1), Ctx(new Dictionary<SecurityId, int> { [S(1)] = 81 }));

        Assert.IsType<ExitVerdict.Hold>(inside);   // rank == ExitRank is still inside
        Assert.IsType<ExitVerdict.Close>(outside);  // strictly past the buffer closes
    }

    /// <summary>The hysteresis property (catalog §6.1): a name entering at rank ≤ N does NOT churn
    /// while it stays inside the wider exit buffer. Enter at 40, drift to 79 with ExitRank 80 → held
    /// every day, never sold. This is what kills boundary-churn cost bleed.</summary>
    [Fact]
    public void FR9_RankBuffer_Hysteresis_ANameDriftingBetweenNAndExitRankDoesNotChurn()
    {
        var policy = new ExitPolicy.RankBuffer(ExitRank: 80);

        foreach (var rank in new[] { 40, 55, 70, 79, 80 })
        {
            var verdict = ExitPolicyExecutor.Evaluate(policy, S(1), Ctx(new Dictionary<SecurityId, int> { [S(1)] = rank }));
            Assert.IsType<ExitVerdict.Hold>(verdict);
        }
    }

    /// <summary>An unscored name is HELD, not closed. "No rank" is the model saying nothing, not
    /// saying sell — closing on a missing input would fail OPEN into an irreversible action, which
    /// inverts rule 10. A name that stops being scorable forever is a delisting, and §13.6 owns
    /// that, not this policy.</summary>
    [Fact]
    public void FR9_RankBuffer_AnUnscoredName_IsHeld_NotClosed()
    {
        var policy = new ExitPolicy.RankBuffer(ExitRank: 80);

        var verdict = ExitPolicyExecutor.Evaluate(policy, S(99), Ctx(new Dictionary<SecurityId, int> { [S(1)] = 1 }));

        var hold = Assert.IsType<ExitVerdict.Hold>(verdict);
        Assert.Contains("not scored", hold.Reason);
    }

    // ============================ ScheduledRebalance ============================

    [Fact]
    public void FR9_ScheduledRebalance_BetweenRebalances_HoldsRegardlessOfTheWishList()
    {
        var policy = new ExitPolicy.ScheduledRebalance(EveryNDays: 21);

        // Session 5 of a 21-day cadence is not a rebalance day. Even a name absent from the wish list
        // is held — rule 7: falling off the wish list closes nothing.
        var verdict = ExitPolicyExecutor.Evaluate(policy, S(1), Ctx(wish: new HashSet<SecurityId>(), sessions: 5));

        var hold = Assert.IsType<ExitVerdict.Hold>(verdict);
        Assert.Contains("not a rebalance day", hold.Reason);
    }

    [Fact]
    public void FR9_ScheduledRebalance_OnARebalanceDay_ClosesWhatIsNoLongerSelected()
    {
        var policy = new ExitPolicy.ScheduledRebalance(EveryNDays: 21);

        // Session 42 IS a rebalance day (42 % 21 == 0). A held name not in the current selection closes.
        var dropped = ExitPolicyExecutor.Evaluate(policy, S(1), Ctx(wish: new HashSet<SecurityId>(), sessions: 42));
        var kept = ExitPolicyExecutor.Evaluate(policy, S(2), Ctx(wish: new HashSet<SecurityId> { S(2) }, sessions: 42));

        Assert.IsType<ExitVerdict.Close>(dropped);
        Assert.IsType<ExitVerdict.Hold>(kept);
    }

    [Fact]
    public void FR9_ScheduledRebalance_Inception_IsARebalanceDay()
    {
        // Session 0 must be a rebalance day, or the first entry is delayed a whole cadence.
        Assert.True(ExitPolicyExecutor.IsRebalanceDay(21, 0));
        Assert.False(ExitPolicyExecutor.IsRebalanceDay(21, 1));
        Assert.True(ExitPolicyExecutor.IsRebalanceDay(21, 21));
    }

    [Fact]
    public void FR9_ScheduledRebalance_NonPositiveCadence_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExitPolicyExecutor.Evaluate(new ExitPolicy.ScheduledRebalance(0), S(1), Ctx()));
    }

    // ============================ the two that refuse ============================

    /// <summary>FR-11/rule 10: the two declared-but-unbuilt shapes REFUSE, naming their Phase-6
    /// owners. TargetOrTimeStop's exitCondition is an opaque token the catalog never defines, so
    /// evaluating it would invent strategy behaviour.</summary>
    [Fact]
    public void FR9_TargetOrTimeStop_IsRefused_NamingItsPhase6Owner()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => ExitPolicyExecutor.Evaluate(new ExitPolicy.TargetOrTimeStop("oversold", 10), S(1), Ctx()));

        Assert.Contains("MeanReversion", ex.Message);
        Assert.Contains("Phase 6", ex.Message);
    }

    [Fact]
    public void FR9_ChannelExit_IsRefused_NamingItsPhase6Owner()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => ExitPolicyExecutor.Evaluate(new ExitPolicy.ChannelExit(20), S(1), Ctx()));

        Assert.Contains("Breakout", ex.Message);
    }
}
