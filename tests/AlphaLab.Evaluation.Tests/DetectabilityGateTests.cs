using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Candidates;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// FR-40/D89 (checkpoint 4.9): the detectability-at-admission gate — refuse a registered candidate
/// whose pre-declared expected effect (net of the trials-budget cost its own admission adds) cannot
/// clear max(analytic NW-MDE at the horizon, the empirical C-1 floor). Fixtures FX-DetectabilityGate-*.
/// </summary>
public class DetectabilityGateTests
{
    private static readonly GateOptions Gate = new();   // Confidence .95, Power .80, horizon 3y

    private static CandidateSpec Spec(string id) => new(id, "momentum", "{}", "{}", 21);

    // σ_LR = 0.001/day ⇒ analytic floor at N'=1: 2.8016·0.001·252/√756 ≈ 2.57%/yr.
    private static void SeedSigma(AlphaLab.Data.AlphaLabDbContext db, double sigma = 0.001, string runKind = "live")
    {
        db.PowerReports.Add(new PowerReportRow
        {
            AsOf = "2026-06-30", StrategyA = "x", StrategyB = "buyhold:cw",
            TDays = 100, SigmaLr = sigma, NwLag = 21, MdeAnn = 0.05, RunKind = runKind,
        });
        db.SaveChanges();
    }

    private static void SeedDetectionPower(AlphaLab.Data.AlphaLabDbContext db, double pAt2 = 0.5, double pAt4 = 0.9)
    {
        // Two swept levels bracketing the power target 0.80 at the 3y horizon (t=756).
        var json = $$"""
            { "alphas_ann_pct": [2, 4],
              "curves": {
                "2": { "knots": [ { "t": 21, "p_promoted": 0.0 }, { "t": 756, "p_promoted": {{pAt2.ToString(CultureInfo.InvariantCulture)}} } ] },
                "4": { "knots": [ { "t": 21, "p_promoted": 0.1 }, { "t": 756, "p_promoted": {{pAt4.ToString(CultureInfo.InvariantCulture)}} } ] }
              } }
            """;
        db.Config.Add(new ConfigRow
        {
            Key = CalibratedKeys.DetectionPower, ValueJson = json, Version = 1, ChangedOn = "2026-06-30",
        });
        db.SaveChanges();
    }

    [Fact]
    public void FX_DetectabilityGate_Refuses_NothingPersisted()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedSigma(db);
        var factory = new CandidateFactory(db, Gate);
        var hid = factory.RegisterHypothesis("2026-07-01", "t", "b", "beta_adjusted_alpha", 252, expectedEffectAnn: 0.01);

        // 1%/yr < the ~2.6%/yr analytic floor ⇒ refused, with the structured details.
        var ex = Assert.Throws<DetectabilityRefusedException>(() =>
            factory.CreateCandidate(Spec("weak:1"), hid, unregistered: false, createdOn: "2026-07-01"));
        Assert.Equal(0.01, ex.Details.ExpectedEffectAnn);
        Assert.True(ex.Details.FloorAnn > 0.02);
        Assert.Equal(3, ex.Details.HorizonYears);
        Assert.Equal(1, ex.Details.TrialsAfterAdmission);

        // Fail closed: NO strategy row, NO trials row (the gate runs before any write).
        Assert.Empty(db.Strategies.ToList());
        Assert.Empty(db.TrialsRegistry.ToList());
    }

    [Fact]
    public void FX_DetectabilityGate_Admits_WhenTheEffectClearsTheFloor()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedSigma(db);
        var factory = new CandidateFactory(db, Gate);
        var hid = factory.RegisterHypothesis("2026-07-01", "t", "b", "beta_adjusted_alpha", 252, expectedEffectAnn: 0.05);

        var strategy = factory.CreateCandidate(Spec("strong:1"), hid, unregistered: false, createdOn: "2026-07-01");
        Assert.Equal("candidate", strategy.Status);
        Assert.Single(db.TrialsRegistry.ToList());
        Assert.Equal(0.05, db.JournalEntries.Single(j => j.EntryId == hid).ExpectedEffectAnn);
    }

    [Fact]
    public void FX_DetectabilityGate_UnregisteredBypasses_UnderThePermanentMarking()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedSigma(db, sigma: 10.0);   // an absurd floor — nothing registered could clear it
        var factory = new CandidateFactory(db, Gate);

        var strategy = factory.CreateCandidate(Spec("adhoc:1"), null, unregistered: true, createdOn: "2026-07-01");
        Assert.Contains("\"unregistered\":true", strategy.ConfigJson.Replace(" ", ""));
    }

    [Fact]
    public void FX_DetectabilityGate_TrialsHaircutBinds()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedSigma(db);
        // 3.5%/yr clears the N'=1 floor (~2.6%) but not the Bonferroni floor at N'=1000 (~4.5%).
        var gate = new DetectabilityGate(db, Gate);
        Assert.True(gate.Assess(0.035).Admitted);

        for (var i = 0; i < 999; i++)
        {
            db.TrialsRegistry.Add(new TrialsRegistryRow
            {
                StrategyId = $"s{i}", RegisteredOn = "2026-01-01", Kind = "new", RunKind = "live",
            });
        }
        db.SaveChanges();

        var ex = Assert.Throws<DetectabilityRefusedException>(() => gate.Assess(0.035));
        Assert.Equal(1000, ex.Details.TrialsAfterAdmission);
        Assert.True(ex.Details.AnalyticMdeAnn > 0.035);
    }

    [Fact]
    public void FX_DetectionPower_EmpiricalFloor_InterpolatesBetweenSweptLevels()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedSigma(db, sigma: 0.0001);   // a tiny analytic floor, so the EMPIRICAL floor binds
        SeedDetectionPower(db);         // P(promoted by 3y): 0.5 @ 2%, 0.9 @ 4% ⇒ α*(0.8) = 3.5%

        var gate = new DetectabilityGate(db, Gate);
        var admitted = gate.Assess(0.04);
        Assert.True(admitted.Admitted);
        Assert.Equal(0.035, admitted.Details!.EmpiricalAlphaStarAnn!.Value, 3);

        var ex = Assert.Throws<DetectabilityRefusedException>(() => gate.Assess(0.03));
        Assert.Equal(0.035, ex.Details.EmpiricalAlphaStarAnn!.Value, 3);
        Assert.Equal(0.035, ex.Details.FloorAnn, 3);

        // If even the TOP swept level never reaches the power, nothing is detectable at the horizon —
        // the empirical floor is unreachable and everything registered is refused (fail closed).
        db.Config.Where(c => c.Key == CalibratedKeys.DetectionPower).ToList().ForEach(c => db.Config.Remove(c));
        db.SaveChanges();
        SeedDetectionPower(db, pAt2: 0.1, pAt4: 0.3);
        var unreachable = Assert.Throws<DetectabilityRefusedException>(() => gate.Assess(0.50));
        Assert.True(double.IsPositiveInfinity(unreachable.Details.FloorAnn));
    }

    [Fact]
    public void FX_DetectabilityGate_NoCurves_AnalyticOnly_AndNoSigma_Unassessed()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var gate = new DetectabilityGate(db, Gate);

        // A pre-calibration, pre-forward lab: no σ anywhere ⇒ UNASSESSED, admits (there is no honest
        // number to refuse against; blocking all research pre-calibration is a different failure).
        var unassessed = gate.Assess(0.001);
        Assert.True(unassessed.Admitted);
        Assert.Equal("unassessed_no_sigma", unassessed.Reason);

        // σ from the REPLAY generation (the calibration vintage) but no C-1 row: analytic-only,
        // visibly marked as the degraded mode.
        SeedSigma(db, runKind: "replay");
        var analyticOnly = gate.Assess(0.05);
        Assert.True(analyticOnly.Admitted);
        Assert.Equal("analytic_only", analyticOnly.Reason);
        Assert.Equal("replay_calibration_median", analyticOnly.Details!.SigmaSource);
        Assert.Null(analyticOnly.Details.EmpiricalAlphaStarAnn);
    }
}
