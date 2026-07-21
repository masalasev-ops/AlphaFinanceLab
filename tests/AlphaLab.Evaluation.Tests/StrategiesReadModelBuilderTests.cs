using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Json;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation;
using AlphaLab.Evaluation.ReadModels;

namespace AlphaLab.Evaluation.Tests;

public class StrategiesReadModelBuilderTests
{
    private static StrategiesReadModelBuilder Builder(AlphaLab.Data.AlphaLabDbContext db) => new(db, new VerdictsOptions());

    [Fact]
    public void Build_BeforeAnyCommittedRun_IsNoRunYet()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();
        var model = Builder(db).Build();
        Assert.Equal(ReadModelStampStatus.NoRunYet, model.Stamp.Status);
    }

    [Fact]
    public void UX1_InsideMde_MetricCell_IsDimmedWithTilde()
    {
        using var arena = new EvalArena();
        // A track PAST MinTrackDays (so TooEarly comes from the MDE, not the track) with a tiny edge under
        // loose pairing ⇒ the gap sits genuinely inside the MDE (noise).
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        var bench = EvalArena.Noise(99, 0.01, seed: 1);
        var candNoise = EvalArena.Noise(99, 0.005, seed: 2);
        var cand = bench.Select((b, i) => b + 0.0002 + candNoise[i]).ToArray();
        arena.SeedStrategy("buyhold:cw", "baseline", dates, bench);
        arena.SeedStrategy("cand:x", "candidate", dates, cand);
        arena.SeedRun(dates[^1]);

        using var db = arena.Open();
        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);   // writes a TooEarly (inside-MDE) power_report

        var row = Builder(db).Build().Rows.Single(r => r.Id == "cand:x");

        Assert.Equal("dimmed", row.Alpha.Display);
        Assert.Equal("~", row.Alpha.Prefix);
        Assert.Equal("inside_mde", row.Alpha.Reason);
        Assert.Equal("TooEarly", row.VerdictChip);
        Assert.Equal("not-yet-distinguishable", row.Tier);

        // The honesty fields serialize snake_case verbatim (a client renders them, computes nothing).
        var json = JsonSerializer.Serialize(row, AlphaLabJson.Options);
        Assert.Contains("\"display\":\"dimmed\"", json);
        Assert.Contains("\"prefix\":\"~\"", json);
        Assert.Contains("\"reason\":\"inside_mde\"", json);
        Assert.Contains("\"seat\":\"math\"", json);
    }

    [Fact]
    public void UX1_ShortTrack_MetricCell_IsDimmed_WithTooEarlyReason_NotInsideMde()
    {
        using var arena = new EvalArena();
        // A short track (29 < MinTrackDays 63) with a decisive constant gap that is OUTSIDE the MDE
        // (σ_LR=0 ⇒ MDE=0): the cell is still dimmed (unproven) but the reason is 'too_early', NOT the
        // misleading 'inside_mde' — the gap is not within noise, there simply isn't enough track yet.
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 29).ToArray());
        arena.SeedStrategy("cand:x", "candidate", dates, Enumerable.Repeat(0.001, 29).ToArray());
        arena.SeedRun(dates[^1]);

        using var db = arena.Open();
        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);

        var row = Builder(db).Build().Rows.Single(r => r.Id == "cand:x");
        Assert.Equal("dimmed", row.Alpha.Display);
        Assert.Equal("~", row.Alpha.Prefix);
        Assert.Equal("too_early", row.Alpha.Reason);   // NOT inside_mde
    }

    [Fact]
    public void FR33_ForwardReadModel_ContainsNoReplayRow()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(80, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("cand:x", "candidate", dates, Enumerable.Repeat(0.0, 79).ToArray());
        arena.SeedRun(dates[^1]);

        using var db = arena.Open();
        // A REPLAY power_report says Promoted; there is NO forward one. The forward read-model must ignore it.
        db.PowerReports.Add(new PowerReportRow
        {
            AsOf = dates[^1], StrategyA = "cand:x", StrategyB = "buyhold:cw",
            TDays = 79, SigmaLr = 0.001, NwLag = 21, MdeAnn = 0.01, ObservedGapAnn = 0.5,
            Verdict = "Promoted", RunKind = "replay",
        });
        db.SaveChanges();

        var row = Builder(db).Build().Rows.Single(r => r.Id == "cand:x");

        Assert.Equal("TooEarly", row.VerdictChip);                 // NOT "Promoted" — the replay row never leaked
        Assert.NotEqual("distinguishable-above", row.Tier);
    }

    [Fact]
    public void Build_Baseline_IsAReferenceRow_WithNoVerdict()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(10, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 9).ToArray());
        arena.SeedRun(dates[^1]);

        using var db = arena.Open();
        var row = Builder(db).Build().Rows.Single(r => r.Id == "buyhold:cw");

        Assert.Equal("reference", row.Tier);
        Assert.Equal("reference", row.VerdictChip);
        Assert.Equal("math", row.Seat);
        Assert.False(row.IsLive);
    }

    [Fact]
    public void Build_RefusedCandidate_IsBelowOrFlagged_NotDistinguishableAbove()
    {
        // A Refused strategy is distinguishable DOWNWARD (a decisive negative gap) — separation_state is
        // direction-agnostic, so the tier must NOT flatter-sort it into 'distinguishable-above' (D63/§20.8).
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(90, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 89).ToArray());
        arena.SeedStrategy("cand:loser", "candidate", dates, Enumerable.Repeat(-0.002, 89).ToArray());   // clear negative edge ⇒ Refused
        arena.SeedRun(dates[^1]);

        using var db = arena.Open();
        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);
        // An S3 row makes the separation path non-empty; with the decisive Refused verdict that yields
        // separation_state='distinguishable' (downward) — the case where the old tier logic misclassified.
        db.OverfittingChecks.Add(new AlphaLab.Data.Entities.OverfittingCheckRow
        {
            StrategyId = "cand:loser", AsOf = dates[^1], Signal = "S3", Value = 40,
            ThresholdJson = "{\"n\":200}", Contribution = "in_band", RunKind = "live",
        });
        db.SaveChanges();

        var row = Builder(db).Build().Rows.Single(r => r.Id == "cand:loser");
        Assert.Equal("Refused", row.VerdictChip);
        Assert.Equal("distinguishable", row.Separation!.State);       // the state IS distinguishable (downward)…
        Assert.Equal("below-or-flagged", row.Tier);                   // …but the TIER is below-or-flagged, not above
    }

    [Fact]
    public void Build_DriftBackFromRefused_IsNotFlatterSortedIntoTheTopTier()
    {
        // A strategy decisively Refused (below-benchmark) at an early eval, then drifted back to TooEarly:
        // its LATEST verdict is TooEarly and separation reverts to 'none', so it must NOT land in the top
        // 'distinguishable-above' tier (the flatter-sort a past-Refused row used to cause).
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(90, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 89).ToArray());
        arena.SeedStrategy("cand:drift", "candidate", dates, Enumerable.Repeat(0.0, 89).ToArray());
        arena.SeedRun(dates[^1]);

        using var db = arena.Open();
        db.PowerReports.Add(new PowerReportRow { AsOf = "2026-01-20", StrategyA = "cand:drift", StrategyB = "buyhold:cw", TDays = 15, SigmaLr = 0.01, NwLag = 21, MdeAnn = 0.1, ObservedGapAnn = -0.5, Verdict = "Refused", RunKind = "live" });
        db.PowerReports.Add(new PowerReportRow { AsOf = "2026-03-20", StrategyA = "cand:drift", StrategyB = "buyhold:cw", TDays = 89, SigmaLr = 0.01, NwLag = 21, MdeAnn = 0.5, ObservedGapAnn = 0.01, Verdict = "TooEarly", RunKind = "live" });
        db.OverfittingChecks.Add(new OverfittingCheckRow { StrategyId = "cand:drift", AsOf = "2026-03-20", Signal = "S3", Value = 50, ThresholdJson = "{\"n\":200}", Contribution = "in_band", RunKind = "live" });
        db.SaveChanges();

        var row = Builder(db).Build().Rows.Single(r => r.Id == "cand:drift");
        Assert.Equal("TooEarly", row.VerdictChip);              // the latest verdict, not the stale Refused
        Assert.Equal("not-yet-distinguishable", row.Tier);      // NOT distinguishable-above
    }

    [Fact]
    public void Build_PromotedCandidate_IsDistinguishableAbove_WithUndimmedAlpha()
    {
        using var arena = new EvalArena();
        var dates = EvalArena.Dates(100, new DateOnly(2026, 1, 5));
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 99).ToArray());
        arena.SeedStrategy("cand:edge", "candidate", dates, Enumerable.Repeat(0.001, 99).ToArray());
        arena.SeedRun(dates[^1]);

        using var db = arena.Open();
        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);   // Promoted ⇒ status live

        var row = Builder(db).Build().Rows.Single(r => r.Id == "cand:edge");

        Assert.Equal("Promoted", row.VerdictChip);
        Assert.Equal("distinguishable-above", row.Tier);
        Assert.True(row.IsLive);
        Assert.Equal("normal", row.Alpha.Display);   // a decisive gap is NOT dimmed
        Assert.Equal("", row.Alpha.Prefix);
    }
}
