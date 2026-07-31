using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.ReadModels;
using AlphaLab.Evaluation.Signals;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// FX-SignalPanel (FR-46/D91): the Signal-Library read-model carries rolling 1y/5y rank-IC, NW bands,
/// the trend flag and its effective sample as RESOLVED fields — the client renders, never computes
/// (rule 18). Plus the finding-292 as-of seam, which is a build-shape requirement rather than a filter.
/// </summary>
public class SignalLibraryBuilderTests
{
    private static readonly SignalLibraryOptions Options = new();

    private static void Register(AlphaLabDbContext db) =>
        db.Signals.Add(new AlphaLab.Data.Entities.SignalRow
        {
            SignalId = "mom:L126", Family = "momentum", ConfigJson = "{\"L\":126}",
            CodeVersion = "v1", RegisteredOn = "2026-01-01",
        });

    private static void Pin(AlphaLabDbContext db, string key, string value, string changedOn) =>
        db.Config.Add(new ConfigRow
        { Key = key, ValueJson = value, Version = 1, ChangedOn = changedOn, Reason = "4.5.2 pin (D108)" });

    /// <summary>`count` daily grades ending at 2026-06-30, each with the given rank-IC.</summary>
    private static void SeedGrades(AlphaLabDbContext db, int horizon, int count, Func<int, double> ic)
    {
        var day = new DateOnly(2026, 6, 30).AddDays(-count);
        for (var i = 0; i < count; i++)
        {
            day = day.AddDays(1);
            db.SignalIc.Add(new SignalIcRow
            {
                SignalId = "mom:L126", AsOf = day.ToString("yyyy-MM-dd"),
                HorizonDays = horizon, RankIc = ic(i), N = 100,
            });
        }
    }

    [Fact]
    public void FX_SignalPanel_ResolvesWindowsFlagAndEffectiveSample_AsFields()
    {
        using var arena = new EvalArena();
        using (var db = arena.Open())
        {
            Register(db);
            Pin(db, SignalLibraryBuilder.GoneAlphaKey, "0.05", "2026-01-01T00:00:00Z");
            Pin(db, SignalLibraryBuilder.DecayAlphaKey, "0.05", "2026-01-01T00:00:00Z");
            SeedGrades(db, 21, 1300, _ => 0.15);
            SeedGrades(db, 63, 1300, _ => 0.15);
            db.SaveChanges();
        }

        using var read = arena.Open();
        var model = new SignalLibraryBuilder(read, Options).Build();

        // One row per (signal, horizon), each carrying BOTH rolling windows.
        Assert.Equal(2, model.Signals.Count);
        var at63 = Assert.Single(model.Signals, s => s.HorizonDays == 63);
        Assert.Equal("momentum", at63.Family);
        Assert.Equal("v1", at63.CodeVersion);
        Assert.Equal([1, 5], at63.Windows.Select(w => w.WindowYears).ToArray());

        // The effective sample is a FIELD, not a caption (D108): 5y/k=63 => 1260/63 = 20.
        var fiveYear = at63.Windows.Single(w => w.WindowYears == 5);
        Assert.Equal(20, fiveYear.EffectiveN);
        Assert.NotNull(fiveYear.BandLo);
        Assert.NotNull(fiveYear.BandHi);
        Assert.True(fiveYear.BandLo < fiveYear.MeanRankIc && fiveYear.MeanRankIc < fiveYear.BandHi);

        // A steady, clearly-positive IC is stable, and the verdict carries what it was judged against.
        Assert.Equal(TrendFlag.Stable, at63.Flag);
        Assert.Null(at63.FlagReason);
        Assert.NotNull(at63.LevelCritical);
        Assert.NotNull(at63.TrendCritical);

        // Reading context rides along — different quantities, never converted.
        Assert.Null(model.DetectionContext);   // no frozen C-1 row in this fixture arena
    }

    [Fact]
    public void BelowTheEffectiveSampleFloor_TheRowSaysInsufficient_WithItsReason()
    {
        // The ramp case: only ~1 year of grades exists, so 5y/k=63 has n_eff 4 — under the floor.
        using var arena = new EvalArena();
        using (var db = arena.Open())
        {
            Register(db);
            Pin(db, SignalLibraryBuilder.GoneAlphaKey, "0.05", "2026-01-01T00:00:00Z");
            Pin(db, SignalLibraryBuilder.DecayAlphaKey, "0.05", "2026-01-01T00:00:00Z");
            SeedGrades(db, 63, 252, _ => 0.20);
            db.SaveChanges();
        }

        using var read = arena.Open();
        var row = Assert.Single(new SignalLibraryBuilder(read, Options).Build().Signals, s => s.HorizonDays == 63);

        Assert.Equal(TrendFlag.Insufficient, row.Flag);
        Assert.Equal(SignalPanelRow.ReasonBelowEffectiveSampleFloor, row.FlagReason);
        Assert.Null(row.TStat);           // no statistic is published below the floor
        Assert.Null(row.LevelCritical);   // and no critical value, because no test was run

        // The MEAN is still reported — what is withheld is the significance claim, not the number.
        Assert.Equal(0.20, row.Windows.Single(w => w.WindowYears == 5).MeanRankIc, 6);
        Assert.Null(row.Windows.Single(w => w.WindowYears == 5).BandLo);
    }

    [Fact]
    public void UnpinnedThresholds_YieldNoVerdict_RatherThanADefaultedOne()
    {
        // A silently-defaulted significance level is exactly the "choose the threshold after seeing the
        // answer" that D108's pin-before-grade rule exists to prevent.
        using var arena = new EvalArena();
        using (var db = arena.Open())
        {
            Register(db);
            SeedGrades(db, 63, 1300, _ => 0.15);
            db.SaveChanges();
        }

        using var read = arena.Open();
        var row = Assert.Single(new SignalLibraryBuilder(read, Options).Build().Signals, s => s.HorizonDays == 63);

        Assert.Equal(TrendFlag.Insufficient, row.Flag);
        Assert.Equal(SignalPanelRow.ReasonNotPinned, row.FlagReason);
    }

    [Fact]
    public void TheDetectabilityFloorRidesWithTheFlag_AndIsWithheldRatherThanDefaultedWhenPowerIsUnpinned()
    {
        // finding 305. `gone` is a failure to reject, so it is uninformative without the effect size the
        // test could have found. The floor ships as a resolved FIELD (rule 18) - and when the power row
        // is absent it is withheld WITH ITS REASON, never quoted at a power nobody chose.
        using var arena = new EvalArena();
        using (var db = arena.Open())
        {
            Register(db);
            SeedGrades(db, 63, 1300, i => i % 2 == 0 ? 0.02 : -0.02);   // mean ~0 => gone
            Pin(db, SignalLibraryBuilder.GoneAlphaKey, "0.05", "2026-01-01T00:00:00Z");
            Pin(db, SignalLibraryBuilder.DecayAlphaKey, "0.05", "2026-01-01T00:00:00Z");
            db.SaveChanges();
        }

        using (var read = arena.Open())
        {
            var row = Assert.Single(new SignalLibraryBuilder(read, Options).Build().Signals, r => r.HorizonDays == 63);
            Assert.Equal(TrendFlag.Gone, row.Flag);                 // a verdict WAS reached...
            Assert.Null(row.MinDetectableIc);                       // ...but the floor is withheld
            Assert.Equal(SignalPanelRow.ReasonPowerNotPinned, row.DetectabilityReason);
            Assert.Null(row.FlagReason);                            // and the VERDICT is not impugned
        }

        using (var db = arena.Open())
        {
            Pin(db, SignalLibraryBuilder.MinDetectablePowerKey, "0.8", "2026-01-02T00:00:00Z");
            db.SaveChanges();
        }

        using var after = arena.Open();
        var pinned = Assert.Single(new SignalLibraryBuilder(after, Options).Build().Signals, r => r.HorizonDays == 63);
        Assert.Equal(TrendFlag.Gone, pinned.Flag);                  // the verdict is unchanged...
        Assert.Null(pinned.DetectabilityReason);
        Assert.NotNull(pinned.MinDetectableIc);                     // ...and now the floor is published
        Assert.True(pinned.MinDetectableIc > 0);
        Assert.NotNull(pinned.StdError);                            // with the error it derives from
    }

    [Fact]
    public void FindingTwoNineTwo_TheAsOfSeam_BoundsBothGradesAndThresholds()
    {
        // The requirement that makes the Phase-5 digest wireable without reopening this phase: a PINNED
        // read must not see a grade written after its as-of, nor a threshold version recorded after it.
        using var arena = new EvalArena();
        using (var db = arena.Open())
        {
            Register(db);
            // Thresholds pinned LATE — after the as-of the pinned read will use.
            Pin(db, SignalLibraryBuilder.GoneAlphaKey, "0.05", "2026-06-01T00:00:00Z");
            Pin(db, SignalLibraryBuilder.DecayAlphaKey, "0.05", "2026-06-01T00:00:00Z");
            SeedGrades(db, 63, 1300, _ => 0.15);
            db.SaveChanges();
        }

        using var read = arena.Open();
        var builder = new SignalLibraryBuilder(read, Options);

        // Live: thresholds resolve (ResolveCurrent) and a verdict is produced.
        var live = Assert.Single(builder.Build().Signals, s => s.HorizonDays == 63);
        Assert.Null(live.FlagReason);
        Assert.Null(builder.Build().AsOf);

        // Pinned BEFORE the thresholds existed: ResolveAsOf finds nothing, so no verdict is emitted —
        // a pack assembled at that as-of cannot inherit a threshold recorded afterwards.
        var early = builder.Build("2026-03-01");
        Assert.Equal("2026-03-01", early.AsOf);
        Assert.Equal(SignalPanelRow.ReasonNotPinned,
            Assert.Single(early.Signals, s => s.HorizonDays == 63).FlagReason);

        // And the grade window is date-bounded too: an as-of early in the series sees fewer rows.
        var earlyWindow = Assert.Single(early.Signals, s => s.HorizonDays == 63)
            .Windows.Single(w => w.WindowYears == 5);
        var liveWindow = live.Windows.Single(w => w.WindowYears == 5);
        Assert.True(earlyWindow.Observations < liveWindow.Observations);
    }

    [Fact]
    public void NoRegisteredSignals_YieldsAnEmptyPanel_NotAFabricatedOne()
    {
        using var arena = new EvalArena();
        using var read = arena.Open();
        var model = new SignalLibraryBuilder(read, Options).Build();
        Assert.Empty(model.Signals);
    }
}
