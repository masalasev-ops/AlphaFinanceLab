using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Ai;
using AlphaLab.Evaluation.Calibration;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// The context pack's AS-OF read of the detectability band (D96/D104, and D116's ceiling from v1.9.71).
/// Separate from <c>DetectabilityGateTests</c> on purpose: the gate resolves CURRENT state because
/// admission is an operational act, this path resolves point-in-time state because a pack may not contain
/// a fact that post-dates its own as-of. The two read paths differ; what they COMPUTE from the row must
/// not, which is why both go through <see cref="DetectionCurves"/>.
/// </summary>
public class AsOfDetectabilityFloorTests
{
    private static readonly GateOptions Gate = new();

    private static void SeedPowerReport(AlphaLab.Data.AlphaLabDbContext db, string asOf, double sigma) =>
        db.PowerReports.Add(new PowerReportRow
        {
            AsOf = asOf, StrategyA = "x", StrategyB = "buyhold:cw",
            TDays = 100, SigmaLr = sigma, NwLag = 21, MdeAnn = 0.05, RunKind = "live",
        });

    private static void SeedCurves(AlphaLab.Data.AlphaLabDbContext db, string changedOn, int version, string levelsJson) =>
        db.Config.Add(new ConfigRow
        {
            Key = CalibratedKeys.DetectionPower, ValueJson = levelsJson, Version = version, ChangedOn = changedOn,
        });

    /// <summary>D116: the pack carries BOTH ends of the band, and the ceiling is resolved AS-OF like the
    /// floor — a pack built on an earlier day must not see a ladder that was widened afterwards, or the
    /// band it showed the seat would not be the band that existed.</summary>
    [Fact]
    public void FX_AsOfDetectabilityFloor_D116_CeilingIsResolvedAsOf_NotCurrent()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedPowerReport(db, "2026-01-31", 0.0001);

        // Version 1 (frozen 2026-02-01): a two-rung ladder ⇒ ceiling 4 × (4/2) = 8%/yr.
        SeedCurves(db, "2026-02-01", 1, """
            { "curves": {
                "2": { "knots": [ { "t": 756, "p_promoted": 0.5 } ] },
                "4": { "knots": [ { "t": 756, "p_promoted": 0.9 } ] } } }
            """);
        // Version 2 (frozen 2026-06-01): the ladder gained a rung ⇒ ceiling 8 × (8/4) = 16%/yr.
        SeedCurves(db, "2026-06-01", 2, """
            { "curves": {
                "2": { "knots": [ { "t": 756, "p_promoted": 0.5 } ] },
                "4": { "knots": [ { "t": 756, "p_promoted": 0.9 } ] },
                "8": { "knots": [ { "t": 756, "p_promoted": 0.95 } ] } } }
            """);
        db.SaveChanges();

        var resolver = new AsOfDetectabilityFloor(db, Gate);

        // A pack dated BEFORE the widening sees the ladder that existed on its day.
        var early = resolver.Resolve("2026-03-15");
        Assert.Equal(0.08, early.CeilingAnn!.Value, 6);
        Assert.Equal(DetectionCurves.CeilingApplied, early.CeilingState);
        Assert.Equal(0.035, early.FloorAnn!.Value, 3);

        // A pack dated after it sees the widened one — same arithmetic, different as-of.
        var late = resolver.Resolve("2026-07-15");
        Assert.Equal(0.16, late.CeilingAnn!.Value, 6);
        Assert.Equal(DetectionCurves.CeilingApplied, late.CeilingState);
    }

    /// <summary>No σ at the as-of ⇒ no honest floor, reported as a REASON rather than a zero. The ceiling
    /// is still resolved and reported: it is a property of the frozen ladder, not of σ, and withholding it
    /// would put the seat back where finding 337 found it — one anchor, pointing up.</summary>
    [Fact]
    public void FX_AsOfDetectabilityFloor_D116_CeilingSurvives_AnUnassessedFloor()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedCurves(db, "2026-02-01", 1, """
            { "curves": {
                "2": { "knots": [ { "t": 756, "p_promoted": 0.5 } ] },
                "4": { "knots": [ { "t": 756, "p_promoted": 0.9 } ] } } }
            """);
        db.SaveChanges();

        var unassessed = new AsOfDetectabilityFloor(db, Gate).Resolve("2026-03-15");
        Assert.Null(unassessed.FloorAnn);
        Assert.Equal("unassessed_no_sigma", unassessed.Reason);
        Assert.Equal(0.08, unassessed.CeilingAnn!.Value, 6);
    }
}
