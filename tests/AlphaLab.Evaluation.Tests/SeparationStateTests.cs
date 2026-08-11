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

    // D148: the rows now carry the bar the monitor judged each point against and its sustain, exactly as
    // OverfittingMonitor writes them — the read-model reads those rather than a literal of its own. A
    // calibrated edge bar of 90 and sustain 2 keeps the fixtures short; the flat form is covered separately.
    private const string Thresholds = "{\"n\":200,\"p_noise_at\":20.0,\"p_edge_at\":90.0,\"sustain_evals\":2}";

    private static void SeedS3Path(AlphaLabDbContext db, string strategyId, params double[] percentiles) =>
        SeedS3PathWith(db, strategyId, Thresholds, percentiles);

    private static void SeedS3PathWith(
        AlphaLabDbContext db, string strategyId, string thresholdJson, params double[] percentiles)
    {
        var existing = db.OverfittingChecks.Count(c => c.StrategyId == strategyId && c.Signal == "S3");
        var day = new DateOnly(2026, 1, 5).AddDays(21 * existing);
        foreach (var p in percentiles)
        {
            db.OverfittingChecks.Add(new OverfittingCheckRow
            {
                StrategyId = strategyId, AsOf = day.ToString("yyyy-MM-dd"), Signal = "S3",
                Value = p, ThresholdJson = thresholdJson, Contribution = "in_band", RunKind = "live",
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

    /// <summary>
    /// D146's consequence at the read-model, which is why the gate boundary mattered beyond the gate.
    /// `SeparationState` treats ANY non-TooEarly verdict as decisive, so the `Refused` a degenerate pair
    /// used to earn suppressed the IndistinguishableFromRandom chip for precisely the strategy most
    /// indistinguishable from random (rule 21). This pins the mechanism from both sides: `Refused`
    /// suppresses it, `TooEarly` does not — so the gate fix genuinely restores the chip rather than
    /// merely relabelling a verdict nobody reads.
    /// </summary>
    [Fact]
    public void D146_ATooEarlyVerdictKeepsTheChip_WhileARefusedOneSuppressesIt()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("degenerate", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        SeedS3Path(db, "degenerate", 50, 51, 49);          // squarely inside the band — no separation

        // No verdict at all: the chip renders, which is the baseline this compares against.
        Assert.True(SeparationState.Resolve(db, "degenerate", Verdicts, "live").IsIndistinguishable);

        // The PRE-D146 verdict for a never-traded pair (gap 0, MDE 0 under a strict `<`).
        SeedVerdict(db, "degenerate", "2026-06-01", "Refused");
        Assert.Equal(SeparationInfo.Distinguishable,
            SeparationState.Resolve(db, "degenerate", Verdicts, "live").State);
        Assert.False(SeparationState.Resolve(db, "degenerate", Verdicts, "live").IsIndistinguishable);

        // The POST-D146 verdict for the same pair. Later AsOf, so it is the latest.
        SeedVerdict(db, "degenerate", "2026-07-01", "TooEarly");
        Assert.True(SeparationState.Resolve(db, "degenerate", Verdicts, "live").IsIndistinguishable);
    }

    private static void SeedVerdict(AlphaLabDbContext db, string strategyId, string asOf, string verdict)
    {
        db.PowerReports.Add(new PowerReportRow
        {
            AsOf = asOf, StrategyA = strategyId, StrategyB = "buyhold:cw", TDays = 100,
            SigmaLr = 0.0, NwLag = 21, MdeAnn = 0.0, ObservedGapAnn = 0.0,
            Verdict = verdict, RunKind = "live",
        });
        db.SaveChanges();
    }

    [Fact]
    public void FX_SeparationChip_EdgePlant_Transitions_None_Emerging_Distinguishable()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("edge", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();

        SeedS3Path(db, "edge", 50, 50);
        Assert.Equal(SeparationInfo.None, SeparationState.Resolve(db, "edge", Verdicts, "live").State);

        // SUSTAINED outside the 25–75 band (§20.8: "the path is sustained outside…"), not one print.
        SeedS3Path(db, "edge", 85, 85);
        Assert.Equal(SeparationInfo.Emerging, SeparationState.Resolve(db, "edge", Verdicts, "live").State);

        // SUSTAINED above P_edge — read from the row (90.0), not a literal 95.
        SeedS3Path(db, "edge", 97, 97);
        Assert.Equal(SeparationInfo.Distinguishable, SeparationState.Resolve(db, "edge", Verdicts, "live").State);
    }

    /// <summary>
    /// D148, the defect this file previously asserted as correct. Every arm of §20.8 was a SINGLE-POINT
    /// test against a hardcoded 95: one print above it read `distinguishable`, one print outside the band
    /// read `emerging`. The spec says "sustained" for both.
    /// </summary>
    [Fact]
    public void D148_ASinglePrintAboveTheEdgeBar_IsNotYetDistinguishable()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("lucky", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        SeedS3Path(db, "lucky", 50, 50, 99);   // one excursion far above the edge bar

        var sep = SeparationState.Resolve(db, "lucky", Verdicts, "live");

        // Not distinguishable (the excursion is not sustained) and not even emerging: the tail is
        // [50, 99], and 50 is inside the band, so neither arm is satisfied. The chip therefore SURVIVES a
        // single lucky print — which is the whole behavioural point of the sustain requirement.
        Assert.Equal(SeparationInfo.None, sep.State);
        Assert.True(sep.IsIndistinguishable);
    }

    [Fact]
    public void D148_ASinglePrintOutsideTheBand_IsNotYetEmerging()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("blip", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        SeedS3Path(db, "blip", 50, 50, 80);   // one print outside 25–75, the two before it inside

        var sep = SeparationState.Resolve(db, "blip", Verdicts, "live");

        Assert.Equal(SeparationInfo.None, sep.State);
        Assert.True(sep.IsIndistinguishable);   // and the chip therefore survives a single excursion
    }

    /// <summary>
    /// THE BAR IS READ, NOT RESTATED. sp500 generation 2's frozen curve runs P_edge(252) = 71.0 — well
    /// BELOW the literal 95 this used — so the doc-conformant rule is looser at that horizon, and the old
    /// hardcode was the CONSERVATIVE error. A path sustained at 75 is distinguishable under the real curve
    /// and was not under the literal; that direction is why fixing this arm alone would have raised the
    /// false-positive rate, and why the sustain requirement had to land with it.
    /// </summary>
    [Fact]
    public void D148_TheEdgeBarComesFromTheRow_NotFromALiteral95()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("calibrated", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        SeedS3PathWith(db, "calibrated",
            "{\"n\":200,\"p_noise_at\":20.0,\"p_edge_at\":71.0,\"sustain_evals\":2}", 75.0, 75.0);

        // 75 is below 95 but ABOVE the frozen P_edge(252) of 71 — sustained, so distinguishable.
        Assert.Equal(SeparationInfo.Distinguishable, SeparationState.Resolve(db, "calibrated", Verdicts, "live").State);
    }

    [Fact]
    public void D148_WithNoCalibratedCurve_TheFlatHealthyAnchorIsUsed_AndItsOwnSustain()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("flat", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        // The pre-calibration row shape: anchors, no curves, no sustain_evals ⇒ FlatAnchorSustainEvals (3).
        var flat = "{\"n\":200,\"healthy_anchor\":95.0,\"suspect_anchor\":25.0}";

        // TWO points against a sustain of three: nothing is sustained yet, so neither arm fires. Fewer
        // points than the sustain is the conservative case and it stays 'none' rather than reaching for
        // the strongest arm the short path could support.
        SeedS3PathWith(db, "flat", flat, 97.0, 97.0);
        Assert.Equal(SeparationInfo.None, SeparationState.Resolve(db, "flat", Verdicts, "live").State);

        SeedS3PathWith(db, "flat", flat, 97.0);   // the third consecutive
        Assert.Equal(SeparationInfo.Distinguishable, SeparationState.Resolve(db, "flat", Verdicts, "live").State);
    }

    [Fact]
    public void D148_AThresholdRowThisBuildCannotRead_CannotClaimSeparation()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("opaque", "candidate", dates, Enumerable.Repeat(0.0, 29).ToArray());

        using var db = arena.Open();
        // No bar of either shape, and malformed JSON: neither can satisfy the arm. Failing toward the
        // CHIP rather than toward a claim of separation is the honest direction.
        SeedS3PathWith(db, "opaque", "{\"n\":200}", 99.0, 99.0, 99.0);
        Assert.NotEqual(SeparationInfo.Distinguishable, SeparationState.Resolve(db, "opaque", Verdicts, "live").State);

        SeedS3PathWith(db, "opaque2", "{ not json", 99.0, 99.0, 99.0);
        Assert.NotEqual(SeparationInfo.Distinguishable, SeparationState.Resolve(db, "opaque2", Verdicts, "live").State);
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
