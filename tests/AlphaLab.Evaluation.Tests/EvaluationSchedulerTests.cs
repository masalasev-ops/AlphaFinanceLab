using AlphaLab.Core.Config;
using AlphaLab.Evaluation;
using AlphaLab.Evaluation.Gate;

namespace AlphaLab.Evaluation.Tests;

public class EvaluationSchedulerTests
{
    private static readonly EvaluationScheduler Sched = new(new GateOptions());   // cadence 21

    [Theory]
    [InlineData(0, false)]    // inception
    [InlineData(1, false)]
    [InlineData(20, false)]
    [InlineData(21, true)]    // first cadence day
    [InlineData(42, true)]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public void IsEvaluationDay_FiresEveryCadence(int session, bool expected) =>
        Assert.Equal(expected, Sched.IsEvaluationDay(session));

    [Fact]
    public void IsEvaluationDay_RespectsConfiguredCadence()
    {
        var weekly = new EvaluationScheduler(new GateOptions { EvaluationCadenceDays = 5 });
        Assert.True(weekly.IsEvaluationDay(5));
        Assert.True(weekly.IsEvaluationDay(10));
        Assert.False(weekly.IsEvaluationDay(7));
    }

    [Theory]
    [InlineData(20, 0, false)]   // not yet one cadence
    [InlineData(21, 0, true)]    // first cadence, none completed
    [InlineData(21, 1, false)]   // this cadence already evaluated
    [InlineData(42, 1, true)]    // second cadence due
    [InlineData(63, 2, true)]
    [InlineData(63, 3, false)]   // fully caught up
    public void IsEvaluationDue_ComparesElapsedCadencesToCompleted(int session, int done, bool expected) =>
        Assert.Equal(expected, Sched.IsEvaluationDue(session, done));

    [Fact]
    public void IsEvaluationDue_SelfHeals_AMissedCadence()
    {
        // A crashed evaluation on the cadence day (0 completed at session 21) is still DUE on the next
        // session — the boundary is not lost until the following cadence.
        Assert.True(Sched.IsEvaluationDue(22, 0));
        // …and once it has been re-driven (1 completed), it stops re-firing until the next boundary.
        Assert.False(Sched.IsEvaluationDue(22, 1));
    }
}

public class PromotionGateTests
{
    [Fact]
    public void ShortTrack_IsTooEarly()
    {
        // A large clear gap still cannot promote before the minimum track.
        Assert.Equal(PromotionVerdict.TooEarly, PromotionGate.Decide(observedGapAnn: 0.50, mdeAnn: 0.05, trackDays: 30, minTrackDays: 63));
    }

    [Fact]
    public void GapInsideTheMde_IsTooEarly()
    {
        // Rule 6: the gate never acts on a gap smaller than the pair's current MDE.
        Assert.Equal(PromotionVerdict.TooEarly, PromotionGate.Decide(observedGapAnn: 0.03, mdeAnn: 0.10, trackDays: 100, minTrackDays: 63));
    }

    [Fact]
    public void GapBeyondTheMde_PromotesOrRefusesBySign()
    {
        Assert.Equal(PromotionVerdict.Promoted, PromotionGate.Decide(0.20, 0.10, 100, 63));
        Assert.Equal(PromotionVerdict.Refused, PromotionGate.Decide(-0.20, 0.10, 100, 63));
    }

    [Fact]
    public void InfiniteMde_IsTooEarly()
    {
        Assert.Equal(PromotionVerdict.TooEarly, PromotionGate.Decide(0.20, double.PositiveInfinity, 100, 63));
    }

    [Theory]
    [InlineData(PromotionVerdict.Promoted, "Promoted")]
    [InlineData(PromotionVerdict.Refused, "Refused")]
    [InlineData(PromotionVerdict.TooEarly, "TooEarly")]
    public void ToToken_MatchesSchemaTokens(PromotionVerdict v, string token) =>
        Assert.Equal(token, PromotionGate.ToToken(v));
}
