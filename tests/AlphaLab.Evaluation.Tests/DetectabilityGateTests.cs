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
    // Confidence .95, Power .80, and the horizon PINNED at 3y rather than inherited from the
    // production default. These fixtures exercise REFUSAL, which needs a floor high enough to refuse
    // with, so they must not move whenever the operator changes how long the lab is willing to wait
    // (D121 made that a policy choice, not a constant). The production default has its own pin:
    // D121_DetectabilityHorizon_DefaultsToTenYears.
    private static readonly GateOptions Gate = new() { DetectabilityHorizonYears = 3 };

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

    /// <summary>The sp500 arena's frozen C-1 shape as of the **2026-07-31 (generation 1)** calibration:
    /// the four monthly ladder rungs with their measured P(promoted) at 3y/15y/20y. Knots are the real
    /// interpolation anchors, so this fixture reproduces those numbers exactly rather than a convenient
    /// caricature of them. **Superseded as the arena's live shape by generation 2** (config version 2,
    /// 2026-08-03) — see <see cref="SeedGeneration2CurveShape"/>. Kept because the branch it exercises
    /// (`floor_unreachable`) is permanent machinery, not a property of this arena.</summary>
    private static void SeedGeneration1CurveShape(AlphaLab.Data.AlphaLabDbContext db)
    {
        const string json = """
            { "alphas_ann_pct": [2, 4, 8, 16],
              "curves": {
                "2":  { "knots": [ { "t": 21, "p_promoted": 0.0 }, { "t": 756, "p_promoted": 0.02 }, { "t": 3780, "p_promoted": 0.02 }, { "t": 5019, "p_promoted": 0.02 } ] },
                "4":  { "knots": [ { "t": 21, "p_promoted": 0.0 }, { "t": 756, "p_promoted": 0.10 }, { "t": 3780, "p_promoted": 0.10 }, { "t": 5019, "p_promoted": 0.10 } ] },
                "8":  { "knots": [ { "t": 21, "p_promoted": 0.0 }, { "t": 756, "p_promoted": 0.30 }, { "t": 3780, "p_promoted": 0.44 }, { "t": 5019, "p_promoted": 0.52 } ] },
                "16": { "knots": [ { "t": 21, "p_promoted": 0.0 }, { "t": 756, "p_promoted": 0.42 }, { "t": 3780, "p_promoted": 0.80 }, { "t": 5019, "p_promoted": 0.86 } ] }
              } }
            """;
        db.Config.Add(new ConfigRow
        {
            Key = CalibratedKeys.DetectionPower, ValueJson = json, Version = 1, ChangedOn = "2026-07-31",
        });
        db.SaveChanges();
    }

    /// <summary>
    /// **Finding 336, pinned — on the GENERATION-1 curve shape, which is now HISTORY.**
    ///
    /// When written this fixture reproduced the arena's live frozen curves. It no longer does: generation 2
    /// froze at config version 2 on 2026-08-03 and its shape is pinned by
    /// <see cref="FX_DetectabilityGate_Generation2Shape_FloorReachable_AtTheD121Horizon"/>. This test is
    /// RETAINED and its numbers are deliberately unchanged, because what it pins is the `floor_unreachable`
    /// BRANCH — that at `Power = 0.80` with no rung reaching it, α* is `+∞` and every pre-registered
    /// candidate is refused rather than admitted on hope. That branch must keep working whether or not this
    /// arena is currently on it; a future arena, or a future generation, can land there again.
    ///
    /// Read it as: "these are the numbers finding 336 measured, and this is what the gate does with them."
    /// Not as a statement about what the sp500 arena admits today.
    /// </summary>
    [Fact]
    public void FX_DetectabilityGate_Generation1Shape_FloorUnreachable_AtTheThenConfiguredHorizon()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedSigma(db, sigma: 0.0001);   // a tiny analytic floor, so the EMPIRICAL end is what binds
        SeedGeneration1CurveShape(db);

        // ---- At the configured 3-year horizon: nothing is admissible, for a reason that is NOT the claim.
        var refused = Assert.Throws<DetectabilityRefusedException>(
            () => new DetectabilityGate(db, Gate).Assess(0.20));
        Assert.Equal("floor_unreachable", refused.Details.Reason);
        Assert.True(double.IsPositiveInfinity(refused.Details.FloorAnn));
        Assert.Contains("no swept C-1 rung reaches power", refused.Message, StringComparison.Ordinal);
        // The ceiling is still REPORTED (the operator needs both ends to read the situation) but cannot
        // bind against an infinite floor — D116's third valve, never an empty band.
        Assert.Equal(0.32, refused.Details.CeilingAnn!.Value, 6);
        Assert.Equal(DetectionCurves.CeilingInert, refused.Details.CeilingState);

        // ---- The same curves at a 15-year horizon: the 16% rung reaches the power, so the floor exists.
        var patient = new DetectabilityGate(db, new GateOptions { DetectabilityHorizonYears = 15 });
        var admitted = patient.Assess(0.20);
        Assert.True(admitted.Admitted);
        Assert.Equal(0.16, admitted.Details!.EmpiricalAlphaStarAnn!.Value, 6);
        Assert.Equal(0.32, admitted.Details.CeilingAnn!.Value, 6);
        Assert.Equal(DetectionCurves.CeilingApplied, admitted.Details.CeilingState);
    }

    /// <summary>The sp500 arena's LIVE frozen C-1 shape: **generation 2**, config version 2, frozen
    /// 2026-08-03T19:22:58Z. Same four rungs, measured on noise the v1.9.77 data fixes cut from 22.04 %/yr
    /// tracking error to ~8 %/yr. Knots at 3y (756), 10y (2520) and the 20y terminal (5019).</summary>
    private static void SeedGeneration2CurveShape(AlphaLab.Data.AlphaLabDbContext db)
    {
        const string json = """
            { "alphas_ann_pct": [2, 4, 8, 16],
              "curves": {
                "2":  { "knots": [ { "t": 21, "p_promoted": 0.0 }, { "t": 756, "p_promoted": 0.04 }, { "t": 2520, "p_promoted": 0.14 }, { "t": 5019, "p_promoted": 0.18 } ] },
                "4":  { "knots": [ { "t": 21, "p_promoted": 0.0 }, { "t": 756, "p_promoted": 0.14 }, { "t": 2520, "p_promoted": 0.52 }, { "t": 5019, "p_promoted": 0.70 } ] },
                "8":  { "knots": [ { "t": 21, "p_promoted": 0.0 }, { "t": 756, "p_promoted": 0.40 }, { "t": 2520, "p_promoted": 0.90 }, { "t": 5019, "p_promoted": 1.00 } ] },
                "16": { "knots": [ { "t": 21, "p_promoted": 0.0 }, { "t": 756, "p_promoted": 0.50 }, { "t": 2520, "p_promoted": 0.98 }, { "t": 5019, "p_promoted": 1.00 } ] }
              } }
            """;
        db.Config.Add(new ConfigRow
        {
            Key = CalibratedKeys.DetectionPower, ValueJson = json, Version = 1, ChangedOn = "2026-08-03",
        });
        db.SaveChanges();
    }

    /// <summary>
    /// **The arena's CURRENT operational state, pinned (v1.9.82).** Generation 2 reopened the gate that
    /// finding 336 found closed, and this is the fixture that keeps that fact visible to the suite — the
    /// same job its generation-1 sibling did for the closed state.
    ///
    /// At the D121 horizon of TEN years the 8 %/yr rung reaches P = 0.90, so α* interpolates to **≈ 6.95 %/yr**
    /// against D116's 32 %/yr ceiling: a live admissible band of roughly [6.95 %, 32 %]. The same curves are
    /// still `floor_unreachable` at THREE years, which is the whole content of D121 — the horizon, not the
    /// data, is what decides whether a real effect can be adjudicated at all.
    ///
    /// The 6.95 % figure is also the check on D121 itself: the horizon was pre-registered from the closed
    /// form (`z·TE/√H` ≈ 7.08 %/yr) before any of these curves existed.
    /// </summary>
    [Fact]
    public void FX_DetectabilityGate_Generation2Shape_FloorReachable_AtTheD121Horizon()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedSigma(db, sigma: 0.0001);   // a tiny analytic floor, so the EMPIRICAL end is what binds
        SeedGeneration2CurveShape(db);

        // ---- At D121's ten-year horizon the gate ADMITS, and the floor is a real number.
        var tenYears = new DetectabilityGate(db, new GateOptions { DetectabilityHorizonYears = 10 });
        var admitted = tenYears.Assess(0.10);
        Assert.True(admitted.Admitted);
        Assert.Equal(0.06947, admitted.Details!.EmpiricalAlphaStarAnn!.Value, 4);
        Assert.Equal(0.32, admitted.Details.CeilingAnn!.Value, 6);
        Assert.Equal(DetectionCurves.CeilingApplied, admitted.Details.CeilingState);

        // A claim BELOW the floor is still refused — reopening the gate did not remove its lower end.
        var tooSmall = Assert.Throws<DetectabilityRefusedException>(() => tenYears.Assess(0.02));
        Assert.Equal(0.06947, tooSmall.Details.EmpiricalAlphaStarAnn!.Value, 4);

        // ---- The SAME curves at three years: still unreachable. D121's content in one assertion.
        var impatient = Assert.Throws<DetectabilityRefusedException>(
            () => new DetectabilityGate(db, Gate).Assess(0.20));
        Assert.Equal("floor_unreachable", impatient.Details.Reason);
        Assert.True(double.IsPositiveInfinity(impatient.Details.FloorAnn));
    }

    /// <summary>D116: the ceiling is `top rung × the ladder's own step` — 16 × (16/8) = 32%/yr here — and
    /// the boundary is INCLUSIVE: a claim equal to it is the last admissible one, not the first refused.</summary>
    [Fact]
    public void FX_DetectabilityGate_D116_CeilingRefusesImplausible_AndAdmitsAtTheBoundary()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        SeedSigma(db, sigma: 0.0001);
        SeedGeneration1CurveShape(db);
        var gate = new DetectabilityGate(db, new GateOptions { DetectabilityHorizonYears = 15 });

        Assert.True(gate.Assess(0.32).Admitted);   // exactly the ceiling — admitted

        var ex = Assert.Throws<DetectabilityRefusedException>(() => gate.Assess(0.3201));
        Assert.Equal("above_ceiling", ex.Details.Reason);
        Assert.Equal(0.32, ex.Details.CeilingAnn!.Value, 6);
        Assert.Contains("plausibility ceiling", ex.Message, StringComparison.Ordinal);

        // The escalation case this decision exists for: a claim nothing in the arena could support.
        var absurd = Assert.Throws<DetectabilityRefusedException>(() => gate.Assess(4.0));
        Assert.Equal("above_ceiling", absurd.Details.Reason);
    }

    /// <summary>D116's valves: a ceiling at or below the floor goes INERT rather than producing an empty
    /// admissible band, and a ladder with no derivable step yields no ceiling at all rather than an
    /// invented one (the finding-309 standard). In both cases the gate admits on the ceiling's account.</summary>
    [Fact]
    public void FX_DetectabilityGate_D116_CeilingInertBelowFloor_AndAbsentWithoutAStep()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        // A large analytic floor (~12.8%/yr) above the [2,4] ladder's ceiling of 4 × (4/2) = 8%/yr.
        SeedSigma(db, sigma: 0.005);
        SeedDetectionPower(db);

        var inert = new DetectabilityGate(db, Gate).Assess(0.20);
        Assert.True(inert.Admitted);   // above the ceiling, but the ceiling cannot bind — never an empty band
        Assert.Equal(0.08, inert.Details!.CeilingAnn!.Value, 6);
        Assert.Equal(DetectionCurves.CeilingInert, inert.Details.CeilingState);

        // One rung ⇒ no ratio ⇒ no step ⇒ no ceiling. Refusing to invent one IS the decision.
        using var single = new EvalArena();
        using var db2 = single.Open();
        SeedSigma(db2, sigma: 0.0001);
        db2.Config.Add(new ConfigRow
        {
            Key = CalibratedKeys.DetectionPower,
            ValueJson = """{ "curves": { "4": { "knots": [ { "t": 756, "p_promoted": 0.9 } ] } } }""",
            Version = 1, ChangedOn = "2026-06-30",
        });
        db2.SaveChanges();

        var noStep = new DetectabilityGate(db2, Gate).Assess(5.0);
        Assert.True(noStep.Admitted);
        Assert.Null(noStep.Details!.CeilingAnn);
        Assert.Equal(DetectionCurves.CeilingNoStep, noStep.Details.CeilingState);
    }

    /// <summary>
    /// D121 (v1.9.79): the horizon is 10 years, and it is load-bearing rather than incidental. The
    /// admission floor is z*TE/sqrt(H), so the horizon decides what may be PROPOSED at all: at 3 years
    /// and generation 2's clean noise the floor is ~13 %/yr against D116's 32 %/yr ceiling, an
    /// admissible band that is entirely too-good-to-be-true for a real equity strategy — the floor
    /// pushing claims UP exactly as the ceiling holds them down. Pinned here because a silent revert
    /// would re-open that escalation channel from below without anything failing.
    /// </summary>
    [Fact]
    public void D121_DetectabilityHorizon_DefaultsToTenYears()
    {
        Assert.Equal(10, new GateOptions().DetectabilityHorizonYears);

        // And the floor really does move with it: halving-ish the horizon raises the minimum
        // detectable effect by sqrt(H) — the relationship the decision turns on.
        static double Floor(double te, int h) =>
            (1.959963985 + 0.8416212336) * te / Math.Sqrt(h);
        Assert.True(Floor(0.08, 10) < Floor(0.08, 3));
        Assert.Equal(0.071, Floor(0.08, 10), 3);   // ~7 %/yr at the chosen horizon
        Assert.Equal(0.129, Floor(0.08, 3), 3);    // ~13 %/yr at the old one
    }
}
