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
        var dates = EvalArena.Dates(30, new DateOnly(2026, 1, 5));   // short track ⇒ TooEarly (inside MDE)
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0, 29).ToArray());
        arena.SeedStrategy("cand:x", "candidate", dates, Enumerable.Repeat(0.001, 29).ToArray());
        arena.SeedRun(dates[^1]);

        using var db = arena.Open();
        new EvaluationStep(db, new GateOptions()).Run(dates[^1]);   // writes a TooEarly power_report

        var model = Builder(db).Build();
        var row = model.Rows.Single(r => r.Id == "cand:x");

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
