using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Monitor;
using AlphaLab.Evaluation.Recompute;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// `FX-RecomputeParity` (MASTER §25.3, D106/D117): before the harness is trusted under corrected rules it
/// must reproduce the generation's existing records EXACTLY under the CURRENT rules, on all three
/// artefacts — statuses, promotions, and would-be-retires.
///
/// **The generation here is synthetic, and that is a necessity rather than a preference.** CI cannot carry
/// the 3.9 GB live store, so these fixtures build a small generation by running the REAL
/// <see cref="OverfittingMonitor"/> and <see cref="EvaluationStep"/> over seeded curves, then recompute it.
/// The live parity run against generation 1 is an operator step (`replay-recompute --verify-parity`) whose
/// output is the actual evidence; this fixture is what stops the harness regressing between such runs.
///
/// **§25.3's failure clause, restated because it governs what to do when one of these goes red:** a parity
/// failure means the harness is NOT USED and generation 2 stands. The equality is never relaxed to a
/// tolerance; the failure routes to finding which input is impure. It is a finding about the store, not a
/// fixture to soften.
/// </summary>
public class RecomputeParityTests
{
    private const string Bench = "buyhold:cw";
    private const string Replay = "replay";
    private static readonly GateOptions Gate = new();

    private static List<string> Dates(int n)
    {
        var start = new DateOnly(2020, 1, 1);
        var dates = new List<string>(n);
        for (var i = 0; i < n; i++) dates.Add(start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return dates;
    }

    /// <summary>A deterministic pseudo-random walk — no Random, so the fixture is reproducible byte for
    /// byte (the house determinism convention).</summary>
    private static double[] Walk(int n, double drift, int seed)
    {
        var r = new double[n];
        var state = (ulong)seed * 6364136223846793005UL + 1442695040888963407UL;
        for (var i = 0; i < n; i++)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            var u = ((state >> 33) % 10_000) / 10_000.0;     // [0,1)
            r[i] = drift + (u - 0.5) * 0.02;
        }
        return r;
    }

    /// <summary>Builds a small replay generation the same way the arena does: seed curves, then RUN the
    /// real monitor and gate session by session so the stored rows are genuine machinery output rather
    /// than hand-written fixtures the recompute could trivially agree with.</summary>
    private static (EvalArena Arena, List<string> Dates, long PopId) SeedGeneration(int days = 150, int evalEvery = 10)
    {
        var arena = new EvalArena();
        var dates = Dates(days);

        arena.SeedStrategy(Bench, "baseline", dates, Walk(days - 1, 0.0004, 1), horizonDays: 21, runKind: Replay);
        // A plant (retire-EXEMPT under replay, D100) and a plain candidate with a modest edge.
        arena.SeedStrategy("plant:edge:monthly:16:0", "candidate", dates, Walk(days - 1, 0.0010, 2), horizonDays: 21, runKind: Replay);
        arena.SeedStrategy("plant:noedge:daily:0:0", "candidate", dates, Walk(days - 1, 0.0004, 3), horizonDays: 21, runKind: Replay);
        arena.SeedStrategy("plant:anti:daily:-2:0", "candidate", dates, Walk(days - 1, -0.0006, 4), horizonDays: 21, runKind: Replay);

        var popId = arena.SeedPopulation("daily", costsOn: true, seed: 7, dates,
            i => Walk(days - 1, 0.0004 + (i - 10) * 0.00002, 100 + i), m: 20, runKind: Replay);

        using (var db = arena.Open())
        {
            var monitor = new OverfittingMonitor(db, Gate);
            var gate = new EvaluationStep(db, Gate);
            for (var i = evalEvery; i < dates.Count; i += evalEvery)
            {
                gate.Run(dates[i], Bench, Replay);
                monitor.Run(dates[i], Bench, popId, Replay);
            }
        }
        return (arena, dates, popId);
    }

    [Fact]
    public void FX_RecomputeParity_ReproducesAllThreeArtefactsExactly_UnderCurrentRules()
    {
        var (arena, _, _) = SeedGeneration();
        using var _a = arena;
        using var db = arena.Open();

        var report = new RecomputeHarness(db, Gate, Replay).Run(RecomputeSpec.Parity);

        // The generation must be non-trivial, or "parity" would be the empty statement.
        Assert.True(report.Statuses.Stored > 0, "the synthetic generation wrote no statuses — the fixture is vacuous");
        Assert.True(report.SubjectsRecomputed >= 3);

        Assert.Equal(0, report.Statuses.Differing);
        Assert.Equal(0, report.Promotions.Differing);
        Assert.Equal(0, report.WouldReverts.Differing);
        Assert.True(report.ParityHolds);
    }

    /// <summary>§25.3's determinism fragment: the recompute is a pure function of the store.</summary>
    [Fact]
    public void FX_RecomputeParity_IsDeterministicOnReRun()
    {
        var (arena, _, _) = SeedGeneration();
        using var _a = arena;
        using var db = arena.Open();
        var harness = new RecomputeHarness(db, Gate, Replay);

        var first = harness.Run(RecomputeSpec.Parity);
        var second = harness.Run(RecomputeSpec.Parity);

        // Field-wise, not record-wise: ArtefactDiff carries an example LIST, and record equality compares
        // a list by reference — two runs would differ on identity alone and the assertion would be about
        // C# rather than about the harness.
        static void Same(ArtefactDiff a, ArtefactDiff b)
        {
            Assert.Equal(a.Artefact, b.Artefact);
            Assert.Equal(a.Stored, b.Stored);
            Assert.Equal(a.Recomputed, b.Recomputed);
            Assert.Equal(a.Differing, b.Differing);
            Assert.Equal(a.Examples, b.Examples);   // sequence equality
        }
        Same(first.Statuses, second.Statuses);
        Same(first.Promotions, second.Promotions);
        Same(first.WouldReverts, second.WouldReverts);
        Assert.Equal(first.ExcludedTruncationLimited, second.ExcludedTruncationLimited);
    }

    /// <summary>
    /// §25.3's sharpest assertion: parity must STILL hold after a new config version is inserted for a key
    /// the recomputed rules read. Without it the fixture passes today by accident — a harness reading
    /// CURRENT config would agree with generation 1 only while nothing had been appended since, and by the
    /// time the harness is used for its purpose that is never true (recording a candidate threshold INSERTs
    /// a version, rule 24 / finding 108).
    /// </summary>
    [Fact]
    public void FX_RecomputeParity_StillHolds_AfterANewConfigVersionIsAppended()
    {
        var (arena, dates, _) = SeedGeneration();
        using var _a = arena;

        using (var seed = arena.Open())
        {
            seed.Config.Add(new ConfigRow
            {
                Key = CalibratedKeys.S6AutoRetireEvals,
                ValueJson = "9",
                Version = 99,
                ChangedOn = dates[^1],   // appended AFTER every session the generation recorded
            });
            seed.SaveChanges();
        }

        using var db = arena.Open();
        var report = new RecomputeHarness(db, Gate, Replay).Run(RecomputeSpec.Parity);
        Assert.True(report.ParityHolds,
            "a config version appended after the generation changed the recomputed answer — the harness is " +
            "reading CURRENT config where §25.1 requires as-of resolution");
    }

    /// <summary>D117 clause 1: report-only. The harness must not write a single row — anywhere.</summary>
    [Fact]
    public void FX_Recompute_WritesNothing()
    {
        var (arena, _, _) = SeedGeneration();
        using var _a = arena;

        int[] Counts()
        {
            using var db = arena.Open();
            return
            [
                db.OverfittingStatus.Count(), db.OverfittingChecks.Count(), db.GoLiveLog.Count(),
                db.PowerReports.Count(), db.EquityCurve.Count(), db.Strategies.Count(), db.Config.Count(),
            ];
        }

        var before = Counts();
        using (var db = arena.Open()) new RecomputeHarness(db, Gate, Replay).Run(RecomputeSpec.Parity);
        Assert.Equal(before, Counts());
    }

    /// <summary>
    /// D117 clause 3 / finding 338: a subject that RETIRED left the promotable set and stopped emitting
    /// rows, so its later sessions were never recorded and the "would not have retired" direction is not
    /// recomputable. It is excluded AND NAMED — a silent exclusion would be the same defect somewhere
    /// quieter. Plants are exempt (D100), which is what keeps the cohorts the curves are built from
    /// recomputable in both directions.
    /// </summary>
    [Fact]
    public void FX_Recompute_ExcludesAndNamesTruncationLimitedSubjects()
    {
        var (arena, dates, _) = SeedGeneration();
        using var _a = arena;

        using (var seed = arena.Open())
        {
            seed.OverfittingStatus.Add(new OverfittingStatusRow
            {
                StrategyId = "plain:retired", AsOf = dates[20], Status = "retired",
                TriggerJson = "{}", RunKind = Replay,
            });
            seed.OverfittingChecks.Add(new OverfittingCheckRow
            {
                StrategyId = "plain:retired", AsOf = dates[20], Signal = "S6", Value = -2.0,
                ThresholdJson = "{}", Contribution = "critical_neg_alpha", RunKind = Replay,
            });
            seed.SaveChanges();
        }

        using var db = arena.Open();
        var report = new RecomputeHarness(db, Gate, Replay).Run(RecomputeSpec.Parity);

        Assert.Contains("plain:retired", report.ExcludedTruncationLimited);
        Assert.True(report.ParityHolds, "excluding the truncation-limited subject must leave parity intact");
    }
}

/// <summary>Tier classification and the refusals (MASTER §25.2 as amended by D117). These are pure over the
/// specification, so they need no arena.</summary>
public class RecomputeSpecTests
{
    private static RecomputeSpec Spec(params (string Key, string Value)[] overrides) =>
        new("t", overrides.ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal));

    [Fact]
    public void Tier_IsTheMaxOverTheParametersTouched()
    {
        Assert.Equal(RecomputeTier.DirectRead, RecomputeSpec.Parity.Tier);
        Assert.Equal(RecomputeTier.DirectRead, Spec((RecomputeParameters.S6SustainEvals, "4")).Tier);
        Assert.Equal(RecomputeTier.EquityDerived, Spec((RecomputeParameters.AlphaDefinition, "jensen")).Tier);
        // max, not first: a DirectRead knob beside an EquityDerived one still needs the equity inputs.
        Assert.Equal(RecomputeTier.EquityDerived,
            Spec((RecomputeParameters.S6SustainEvals, "4"), (RecomputeParameters.AlphaDefinition, "jensen")).Tier);
    }

    /// <summary>
    /// **Finding 340.** §25.1 recorded that `insideCentralBand` is recoverable from the S6 contribution
    /// token. That holds only for rows that did NOT take the negative-alpha branch — `MonitorSignals.S6`
    /// returns EARLY on `t &lt; S6NegativeAlphaT` and never evaluates band membership, so those rows record
    /// none. Move that threshold and exactly those rows fall through to a band check whose input was never
    /// stored. So the knob finding 280 most points at is DerivedBand, not DirectRead — and classifying it
    /// DirectRead is the specific way a v1 harness would look correct and be wrong.
    /// </summary>
    [Fact]
    public void S6NegativeAlphaT_IsDerivedBand_NotDirectRead()
    {
        Assert.Equal(RecomputeTier.DerivedBand, Spec((RecomputeParameters.S6NegativeAlphaT, "-1.5")).Tier);
        Assert.Equal(RecomputeTier.DerivedBand, Spec((RecomputeParameters.S6BandLowPct, "20")).Tier);
    }

    [Fact]
    public void UnknownParameter_IsRefused_NeverGuessed()
    {
        var ex = Assert.Throws<RecomputeRefusedException>(() => Spec(("monitor.s9.invented_knob", "1")).Tier);
        Assert.Contains("unknown parameter", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>§25.2: a harness reading only the stored columns would APPEAR to cover the band case and
    /// quietly return a wrong answer. This version refuses it out loud instead.</summary>
    [Fact]
    public void DerivedBandSpec_IsRefused_RatherThanAnsweredFromStoredColumns()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var ex = Assert.Throws<RecomputeRefusedException>(() =>
            new RecomputeHarness(db, new GateOptions(), "replay").Run(Spec((RecomputeParameters.S6NegativeAlphaT, "-1.5"))));
        Assert.Contains("DerivedBand", ex.Message, StringComparison.Ordinal);
    }
}
