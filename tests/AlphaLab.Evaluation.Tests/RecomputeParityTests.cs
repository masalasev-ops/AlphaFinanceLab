using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Monitor;
using AlphaLab.Evaluation.Recompute;
using AlphaLab.Evaluation.Populations;

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
                monitor.Run(dates[i], Bench, PopulationMatcher.Fixed(popId), Replay);
            }
            // …and one final session ON THE LAST DATE. Not cosmetic: EvalArena seeds the whole equity curve
            // up front, so a monitor run at session i reads the FULL curve rather than the curve as it stood
            // at i — the fixture is not point-in-time the way a real replay is. The two coincide only where
            // the session IS the last date, which is the one place a derived-band recompute can be compared
            // against a stored value here (FX_DerivedBand_TStatMatchesStored_AtTheFinalSession).
            gate.Run(dates[^1], Bench, Replay);
            monitor.Run(dates[^1], Bench, PopulationMatcher.Fixed(popId), Replay);
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

    /// <summary>
    /// v1.9.73: the harness recomputes the **C-1 detection-power curve**, which is what turns "promotions
    /// changed" into an answer about the GATE. Under the parity spec both sides must be identical — the
    /// recomputed curve is derived from the recomputed promotions, and parity says those equal the stored
    /// ones, so any divergence here is the CURVE arithmetic disagreeing with itself rather than a result.
    /// </summary>
    [Fact]
    public void FX_RecomputeDetectionPower_UnderParity_StoredAndRecomputedCurvesAgree()
    {
        var (arena, _, _) = SeedGeneration();
        using var _a = arena;
        using var db = arena.Open();

        var report = new RecomputeHarness(db, Gate, Replay).Run(RecomputeSpec.Parity);
        var dp = report.DetectionPower;

        Assert.NotNull(dp);
        Assert.NotEmpty(dp!.Rungs);                       // the seeded monthly:16 cohort is the sweep
        Assert.Equal(Gate.DetectabilityHorizonYears, dp.HorizonYears);
        foreach (var rung in dp.Rungs)
        {
            Assert.Equal(rung.StoredPromoted, rung.RecomputedPromoted);
            Assert.Equal(rung.StoredPAtHorizon, rung.RecomputedPAtHorizon);
            Assert.Equal(rung.StoredMedianSessions, rung.RecomputedMedianSessions);
        }
        Assert.Equal(dp.StoredAlphaStarAnn, dp.RecomputedAlphaStarAnn);
    }

    /// <summary>The promotion breakdown distinguishes moved / gained / LOST. Under parity all three are
    /// zero — the assertion that the classifier does not invent movement where there is none.</summary>
    [Fact]
    public void FX_RecomputePromotionShape_UnderParity_IsAllZero()
    {
        var (arena, _, _) = SeedGeneration();
        using var _a = arena;
        using var db = arena.Open();

        var shape = new RecomputeHarness(db, Gate, Replay).Run(RecomputeSpec.Parity).PromotionShape;

        Assert.Equal(0, shape.Moved);
        Assert.Equal(0, shape.Gained);
        Assert.Equal(0, shape.Lost);
        Assert.Empty(shape.LostSubjects);
    }

    /// <summary>
    /// A LOST promotion — an edge the old rule found and the new one does not — is the direction that
    /// argues AGAINST a rule change, so it is listed in FULL and never sampled. Provoked by planting a
    /// stored promotion for a subject the recompute will not promote.
    /// </summary>
    [Fact]
    public void FX_RecomputePromotionShape_ListsEveryLostPromotion_NeverSampled()
    {
        var (arena, dates, _) = SeedGeneration();
        using var _a = arena;

        using (var seed = arena.Open())
        {
            seed.GoLiveLog.Add(new GoLiveLogRow
            {
                AsOf = dates[30], Promoted = "plant:noedge:daily:0:0", Verdict = "Promoted",
                EvidenceJson = "{}", RunKind = Replay,
            });
            seed.SaveChanges();
        }

        using var db = arena.Open();
        var report = new RecomputeHarness(db, Gate, Replay).Run(RecomputeSpec.Parity);

        Assert.Equal(1, report.PromotionShape.Lost);
        Assert.Contains(report.PromotionShape.LostSubjects, s => s.Contains("plant:noedge:daily:0:0", StringComparison.Ordinal));
        Assert.Contains(report.Promotions.Examples, e => e.Contains("LOST", StringComparison.Ordinal));
    }

    /// <summary>
    /// The finding-280 instrument (v1.9.73). Finding 280 is that anti-predictive and merely-edgeless plants
    /// are flagged at IDENTICAL rates, so only the DIFFERENTIAL judges a fix — a change suppressing both
    /// equally has fixed nothing while the raw status count falls and looks like progress. Under parity the
    /// separation must be unchanged, and both cohorts must actually be present, or the instrument is
    /// measuring nothing.
    /// </summary>
    [Fact]
    public void FX_CohortSeparation_UnderParity_IsUnchanged_AndBothCohortsPresent()
    {
        var (arena, _, _) = SeedGeneration();
        using var _a = arena;
        using var db = arena.Open();

        var sep = new RecomputeHarness(db, Gate, Replay).Run(RecomputeSpec.Parity).Separation;

        Assert.NotNull(sep);
        // The non-saturating instrument (finding 346) is present and unchanged under parity.
        Assert.NotEmpty(sep!.Speeds);
        foreach (var sp in sep.Speeds)
        {
            Assert.Equal(sp.StoredMedianSessions, sp.RecomputedMedianSessions);
            Assert.Equal(sp.StoredNeverFlagged, sp.RecomputedNeverFlagged);
        }
        Assert.Equal(sep.SpeedGap.Stored, sep.SpeedGap.Recomputed);

        // Reported at SEVERAL horizons, never one: the ever-Suspect predicate saturates over a long window
        // (finding 343), so a single full-window number would discriminate nothing.
        Assert.True(sep.Horizons.Count >= 3);
        Assert.Contains(sep.Horizons, h => h.Sessions is null);        // the full window is carried
        Assert.Contains(sep.Horizons, h => h.Sessions == 252);         // ...and a short one, to expose saturation
        foreach (var h in sep.Horizons)
        {
            Assert.Contains(h.Cohorts, c => c.Kind == "anti");
            Assert.Contains(h.Cohorts, c => c.Kind == "noedge");
            foreach (var c in h.Cohorts) Assert.Equal(c.StoredEverSuspect, c.RecomputedEverSuspect);
            Assert.Equal(h.StoredSeparation, h.RecomputedSeparation);
        }
    }

    /// <summary>The denominator comes from the RECOMPUTED SUBJECTS, not the strategies table: a plant that
    /// was never simulated (finding 341's pre-Change-4 residue) cannot be flagged, and counting it would
    /// silently deflate the rate it lands in.</summary>
    [Fact]
    public void FX_CohortSeparation_DenominatorExcludesNeverSimulatedPlants()
    {
        var (arena, _, _) = SeedGeneration();
        using var _a = arena;

        using (var seed = arena.Open())
        {
            // A plant row with no account, no curve and no checks — exactly finding 341's shape.
            seed.Strategies.Add(new StrategyRow
            {
                StrategyId = "plant:noedge:daily:0:999", Family = "test", ConfigJson = "{}",
                ExitPolicyJson = "{}", CreatedOn = "2020-01-01", Status = "candidate",
            });
            seed.SaveChanges();
        }

        using var db = arena.Open();
        var sep = new RecomputeHarness(db, Gate, Replay).Run(RecomputeSpec.Parity).Separation;
        var noEdge = Assert.Single(sep!.Horizons[^1].Cohorts, c => c.Kind == "noedge");

        Assert.DoesNotContain("999", noEdge.Cohort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(db.OverfittingChecks.Where(c => c.RunKind == Replay)
            .Select(c => c.StrategyId).Distinct().AsEnumerable()
            .Count(id => id.StartsWith("plant:noedge:", StringComparison.Ordinal)), noEdge.Cohort);
    }

    /// <summary>
    /// **The check that earns the `derived-band` tier (v1.9.75).** The tier's whole premise is that it can
    /// re-derive the 63-day window the monitor used; the way to believe that is to reproduce a quantity the
    /// monitor RECORDED — the S6 t-statistic stored in `overfitting_checks.value`.
    ///
    /// **Asserted at the FINAL session only, and the reason is a property of the fixture, not of the tier.**
    /// `EvalArena.SeedStrategy` writes the whole equity curve up front, so when this fixture runs the monitor
    /// at session *i* the monitor reads the FULL curve — future rows included — and its stored value is the
    /// tail of the whole series, not the tail as of *i*. A real replay grows day by day, so its stored values
    /// ARE point-in-time. At the last session the two coincide exactly, which makes it the one session where
    /// a fixture built this way can compare the two honestly. The full-generation comparison is the LIVE
    /// operator run, and that is the one the tier is actually earned by.
    /// </summary>
    [Fact]
    public void FX_DerivedBand_TStatMatchesStored_AtTheFinalSession()
    {
        var (arena, dates, _) = SeedGeneration();
        using var _a = arena;
        using var db = arena.Open();

        var lastSession = db.OverfittingChecks
            .Where(c => c.RunKind == Replay && c.Signal == "S6")
            .Select(c => c.AsOf).AsEnumerable().Max(StringComparer.Ordinal)!;

        var subjects = db.OverfittingChecks.Where(c => c.RunKind == Replay)
            .Select(c => c.StrategyId).Distinct().ToList();
        var bands = BandInputs.Build(db, subjects, Bench, Replay);
        Assert.NotNull(bands);

        var compared = 0;
        foreach (var row in db.OverfittingChecks
                     .Where(c => c.RunKind == Replay && c.Signal == "S6" && c.AsOf == lastSession && c.Value != null)
                     .Select(c => new { c.StrategyId, c.Value })
                     .AsEnumerable())
        {
            var window = bands!.StrategyWindow(row.StrategyId, lastSession);
            Assert.NotNull(window);
            Assert.Equal(row.Value!.Value, window!.Value.T, 9);
            compared++;
        }
        Assert.True(compared >= 3, $"only {compared} S6 rows compared — the fixture is too thin to mean anything");
    }

    /// <summary>The window is POINT-IN-TIME: an earlier session's window must not equal a later one's on a
    /// series that is still moving. Without this, a bug that ignored the as-of would pass the t-stat check
    /// above (which is deliberately taken at the last session) and be wrong everywhere else.</summary>
    [Fact]
    public void FX_DerivedBand_WindowIsPointInTime()
    {
        var (arena, dates, _) = SeedGeneration();
        using var _a = arena;
        using var db = arena.Open();

        var subjects = new[] { "plant:edge:monthly:16:0" };
        var bands = BandInputs.Build(db, subjects, Bench, Replay)!;

        var early = bands.StrategyWindow(subjects[0], dates[100]);
        var late = bands.StrategyWindow(subjects[0], dates[^1]);

        Assert.NotNull(early);
        Assert.NotNull(late);
        Assert.NotEqual(early!.Value.Alpha, late!.Value.Alpha, 6);

        // …and before the window is full there is no answer at all — the monitor's `insufficient_track`.
        Assert.Null(bands.StrategyWindow(subjects[0], dates[10]));
    }

    /// <summary>
    /// **Finding 340, made executable.** Under the STORED threshold a row took the negative-alpha branch and
    /// therefore recorded no band token. Move the threshold so it falls through, and the answer must come
    /// from the DERIVED band — with no band inputs the harness must refuse rather than recover a token that
    /// does not mean what it would need to mean.
    /// </summary>
    [Fact]
    public void FX_DerivedBand_MovedThreshold_RefusesWithoutInputs_AndAnswersWithThem()
    {
        var (arena, _, _) = SeedGeneration();
        using var _a = arena;
        using var db = arena.Open();

        var moved = new RecomputeSpec("s6-negt",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RecomputeParameters.S6NegativeAlphaT] = "-1.5",
            });
        Assert.Equal(RecomputeTier.DerivedBand, moved.Tier);

        // No inputs supplied ⇒ refused, out loud.
        var subjects = db.OverfittingChecks.Where(c => c.RunKind == Replay)
            .Select(c => c.StrategyId).Distinct().ToList();
        var ex = Assert.Throws<RecomputeRefusedException>(
            () => new MonitorRecompute(db, moved, Replay).Run(subjects));
        Assert.Contains("none were supplied", ex.Message, StringComparison.Ordinal);

        // Inputs supplied ⇒ it answers, and the harness wires them for a band-tier spec.
        var withBands = new MonitorRecompute(db, moved, Replay, BandInputs.Build(db, subjects, Bench, Replay))
            .Run(subjects);
        Assert.NotEmpty(withBands);

        var viaHarness = new RecomputeHarness(db, Gate, Replay).Run(moved);
        Assert.Equal(RecomputeTier.DerivedBand, viaHarness.Tier);
        Assert.True(viaHarness.Statuses.Recomputed > 0);
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
        Assert.Equal(RecomputeTier.DerivedBand, Spec((RecomputeParameters.S6NegativeAlphaT, "-1.5")).Tier);
        // max, not first: a DirectRead knob beside a DerivedBand one still needs the band inputs.
        Assert.Equal(RecomputeTier.DerivedBand,
            Spec((RecomputeParameters.S6SustainEvals, "4"), (RecomputeParameters.S6BandLowPct, "20")).Tier);
    }

    /// <summary>
    /// P23 — THE EFFECT DEFINITION IS NOT A KNOB A SPECIFICATION MAY SET, and a supplied one is REFUSED
    /// rather than silently honoured.
    ///
    /// It used to be an accepted `EquityDerived` parameter defaulting to `raw_gap` — generation 1's
    /// arithmetic, applied to every stored generation forever. Pointed at generation 2 (frozen under
    /// jensen), a parity run reported **94 differing promotions** and an α* of 6.56 %/yr against a frozen
    /// 6.95 %/yr, and archived a report that read exactly like evidence. One flag made all 94 vanish.
    ///
    /// The definition is a PROPERTY OF THE STORED GENERATION, so the harness resolves it from what
    /// actually reproduces. Honouring an operator's guess is what produced a confident wrong answer, so
    /// the guess is refused on the D139 fail-closed pattern — and the refusal is free, because an
    /// unclassifiable parameter was already refused. Removing it from the tier map IS the fix.
    /// </summary>
    [Fact]
    public void P23_ASuppliedAlphaDefinition_IsRefused_NeverSilentlyHonoured()
    {
        foreach (var value in new[] { "jensen", "raw_gap" })
        {
            var ex = Assert.Throws<RecomputeRefusedException>(
                () => _ = Spec(("gate.alpha_definition", value)).Tier);
            Assert.Contains("gate.alpha_definition", ex.Message, StringComparison.Ordinal);
        }

        // ...and it is gone from the accepted set entirely, so no caller can reach it by another name.
        Assert.DoesNotContain("alpha_definition", string.Join(",", RecomputeParameters.Tiers.Keys), StringComparison.Ordinal);
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
