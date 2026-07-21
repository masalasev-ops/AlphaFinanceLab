using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.ReadModels;

namespace AlphaLab.Evaluation.Tests;

public class SeparationStateTests
{
    // A short min-track so a 30-day fixture can cross it.
    private static readonly VerdictsOptions Verdicts = new() { SeparationMinTrackDays = 10, SeparationBandCentralFrac = 0.50 };

    private static void SeedS3Path(AlphaLabDbContext db, string strategyId, params double[] percentiles)
    {
        var day = new DateOnly(2026, 1, 5);
        foreach (var p in percentiles)
        {
            db.OverfittingChecks.Add(new OverfittingCheckRow
            {
                StrategyId = strategyId, AsOf = day.ToString("yyyy-MM-dd"), Signal = "S3",
                Value = p, ThresholdJson = "{\"n\":200}", Contribution = "in_band", RunKind = "live",
            });
            day = day.AddDays(21);
        }
        db.SaveChanges();
    }

    [Fact]
    public void FX_SeparationChip_NoEdge_RendersNone_WithItsDayCount_PastTheMinTrack()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));   // 30 equity points ⇒ 30 track days
        arena.SeedStrategy("noedge", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        SeedS3Path(db, "noedge", 50, 48, 52, 49);                     // a no-edge path hovering at the median

        var sep = SeparationState.Resolve(db, "noedge", Verdicts, "live");

        Assert.Equal(SeparationInfo.None, sep.State);
        Assert.Equal(30, sep.Days);
        Assert.True(sep.IsIndistinguishable);                        // renders the IndistinguishableFromRandom chip
    }

    [Fact]
    public void FX_SeparationChip_EdgePlant_Transitions_None_Emerging_Distinguishable()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("edge", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();

        SeedS3Path(db, "edge", 50);
        Assert.Equal(SeparationInfo.None, SeparationState.Resolve(db, "edge", Verdicts, "live").State);

        SeedS3Path(db, "edge", 85);                                   // latest now outside the 25–75 band
        Assert.Equal(SeparationInfo.Emerging, SeparationState.Resolve(db, "edge", Verdicts, "live").State);

        SeedS3Path(db, "edge", 97);                                   // latest above the 95th anchor
        Assert.Equal(SeparationInfo.Distinguishable, SeparationState.Resolve(db, "edge", Verdicts, "live").State);
    }

    [Fact]
    public void UX12_SeparationChip_RendersWhenTrackExceedsMinAndStateNone()
    {
        // State none + track ≥ min ⇒ chip; track < min ⇒ no chip (not enough evidence to make the claim).
        var past = new SeparationInfo(SeparationInfo.None, Days: 300, MinTrackDays: 252);
        var early = new SeparationInfo(SeparationInfo.None, Days: 100, MinTrackDays: 252);
        var distinguishable = new SeparationInfo(SeparationInfo.Distinguishable, Days: 300, MinTrackDays: 252);

        Assert.True(past.IsIndistinguishable);
        Assert.False(early.IsIndistinguishable);
        Assert.False(distinguishable.IsIndistinguishable);           // a distinguishable strategy never shows the chip
    }

    [Fact]
    public void DecisiveGateVerdict_MakesItDistinguishable_EvenInsideTheBand()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("won", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        SeedS3Path(db, "won", 55);   // still in-band on S3…
        db.PowerReports.Add(new PowerReportRow
        {
            AsOf = dates[^1], StrategyA = "won", StrategyB = "buyhold:cw", TDays = 29, SigmaLr = 0.001,
            NwLag = 21, MdeAnn = 0.01, ObservedGapAnn = 0.2, Verdict = "Promoted", RunKind = "live",
        });
        db.SaveChanges();

        // …but a decisive gate verdict means the pair IS distinguishable (D63/§20.8).
        Assert.Equal(SeparationInfo.Distinguishable, SeparationState.Resolve(db, "won", Verdicts, "live").State);
    }

    [Fact]
    public void DriftBackToTooEarly_RevertsToNone_NotPinnedByAPastDecisiveVerdict()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("s", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        SeedS3Path(db, "s", 50);   // S3 back in-band
        // An EARLIER decisive Refused, then a LATER TooEarly — the cumulative gap decayed back inside the MDE.
        db.PowerReports.Add(new PowerReportRow { AsOf = "2026-01-10", StrategyA = "s", StrategyB = "buyhold:cw", TDays = 20, SigmaLr = 0.01, NwLag = 21, MdeAnn = 0.1, ObservedGapAnn = -0.5, Verdict = "Refused", RunKind = "live" });
        db.PowerReports.Add(new PowerReportRow { AsOf = "2026-02-10", StrategyA = "s", StrategyB = "buyhold:cw", TDays = 30, SigmaLr = 0.01, NwLag = 21, MdeAnn = 0.5, ObservedGapAnn = 0.01, Verdict = "TooEarly", RunKind = "live" });
        db.SaveChanges();

        var sep = SeparationState.Resolve(db, "s", Verdicts, "live");

        // The LATEST verdict is TooEarly ⇒ not decisive; a single historical Refused must NOT pin it to
        // 'distinguishable' — the IndistinguishableFromRandom chip reappears (track ≥ min).
        Assert.Equal(SeparationInfo.None, sep.State);
        Assert.True(sep.IsIndistinguishable);
    }

    [Fact]
    public void Separation_ReconstructsFromThePersistedRows_Deterministically()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("s", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        SeedS3Path(db, "s", 60, 45, 51);

        var a = SeparationState.Resolve(db, "s", Verdicts, "live");
        var b = SeparationState.Resolve(db, "s", Verdicts, "live");
        Assert.Equal(a, b);   // pure function of the persisted percentile rows (NFR-2)
    }
}
