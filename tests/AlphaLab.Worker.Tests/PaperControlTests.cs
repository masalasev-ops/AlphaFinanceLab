using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Ai;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The D113 paper control (checkpoint 5.7): the arm difference, the same-run floor, and the budget
/// pairing.
///
/// **Every assertion here is about a way the control could silently stop being a control** — two
/// identical arms, two different floors, or one arm without the other. None of those would throw; each
/// would produce a margin series that looks fine and means nothing.
/// </summary>
public class PaperControlTests
{
    private static PipelineHarness Harness(IAnalysisProvider provider, decimal monthlyBudgetUsd = 5.0m) =>
        new(configure: s =>
        {
            s.AddScoped(_ => provider);
            s.AddSingleton(new AiOptions
            {
                Researcher = new ResearcherSeatOptions { MonthlyBudgetUsd = monthlyBudgetUsd },
            });
            foreach (var kind in ResearchJobExecutor.Kinds)
            {
                var k = kind;
                s.AddSingleton<IJobExecutor>(sp => new ResearchJobExecutor(
                    k,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResearchJobExecutor>>()));
            }
        });

    private static void QueueProposal(PipelineHarness h)
    {
        using var db = h.Open();
        db.Jobs.Add(new JobRow
        {
            Kind = "analysis_hypotheses", Status = "queued", SubmittedAt = "2026-08-01T10:00:00Z",
            RequestJson = """{"ParentFinding":"f-311","PriorProb":0.35}""",
        });
        db.SaveChanges();
    }

    /// <summary>A σ estimate so the arena has a computable floor to stamp on both arms.</summary>
    private static void SeedSigma(PipelineHarness h)
    {
        using var db = h.Open();
        db.PowerReports.Add(new PowerReportRow
        {
            AsOf = "2026-07-31", StrategyA = "cand:a", StrategyB = "buyhold:cw", TDays = 500,
            SigmaLr = 0.0008, NwLag = 21, MdeAnn = 0.03, ObservedGapAnn = 0.0,
            Verdict = "TooEarly", RunKind = "replay",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task D113_OneJobRun_WritesBothArms_StampedWithTheSameFloor()
    {
        var provider = new RecordingProvider();
        using var h = Harness(provider);
        SeedSigma(h);
        QueueProposal(h);

        var outcome = await h.RunJobDrainAsync();
        Assert.Equal(1, outcome.Done);

        using var db = h.Open();
        var entries = db.JournalEntries.OrderBy(e => e.EntryId).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Title.Contains("[treatment]", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Title.Contains("[control]", StringComparison.Ordinal));

        // THE constraint the same-run rule exists for: the floor resolves from CURRENT state, so an
        // admission between the arms would move it by one Bonferroni step and the difference would stop
        // being clean. Both arms carry one number because it was read once.
        Assert.All(entries, e => Assert.NotNull(e.DetectabilityFloorAnn));
        Assert.Equal(entries[0].DetectabilityFloorAnn, entries[1].DetectabilityFloorAnn);

        // Both carry the stated prior, and both are UNLOCKED — the control is a proposal like any other,
        // which is precisely why it is free: the trials tax is paid at ADMISSION.
        Assert.All(entries, e => Assert.Equal(0.35, e.PriorProb));
        Assert.All(entries, e => Assert.False(e.Locked));
        Assert.Empty(db.TrialsRegistry.ToList());
        Assert.Empty(db.Strategies.ToList());
    }

    [Fact]
    public async Task D113_TheArmsDifferOnlyInTheSeam()
    {
        // The arm difference is the whole content of the decision. Two identical researchers produce a
        // margin difference of zero by construction, so this asserts the prompts actually differ — and
        // differ ONLY on the seam line, so a measured difference cannot be attributed to anything else.
        var provider = new RecordingProvider();
        using var h = Harness(provider);
        SeedSigma(h);
        QueueProposal(h);

        await h.RunJobDrainAsync();

        Assert.Equal(2, provider.Requests.Count);
        var (treatment, control) = (provider.Requests[0], provider.Requests[1]);

        // L0 and L1 are byte-identical: the cached prefix must not move between arms, or the control
        // would be cheaper than the treatment for a reason unrelated to the seam.
        Assert.Equal(treatment.Prompt.StaticInstructions, control.Prompt.StaticInstructions);
        Assert.Equal(treatment.Prompt.LessonSet, control.Prompt.LessonSet);

        var tLines = treatment.Prompt.Fresh.Split('\n');
        var cLines = control.Prompt.Fresh.Split('\n');
        Assert.Equal(tLines.Length, cLines.Length);

        var differing = tLines.Zip(cLines).Where(p => p.First != p.Second).ToList();
        var line = Assert.Single(differing);
        Assert.Contains("Evidence-prior seam", line.First, StringComparison.Ordinal);
        Assert.Contains("on", line.First, StringComparison.Ordinal);
        Assert.Contains("placebo", line.Second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FX_BudgetAbstain_AnExhaustedMonthAbstainsBOTHArms_NeverOne()
    {
        // The sharper risk is not the doubling but the UNPAIRING: a treatment proposal with no control is
        // an unpaired observation silently entering the margin series — worse than no observation,
        // because it looks like one.
        var provider = new RecordingProvider();
        using var h = Harness(provider, monthlyBudgetUsd: 0.10m);   // less than one pair
        SeedSigma(h);
        QueueProposal(h);

        var outcome = await h.RunJobDrainAsync();

        Assert.Equal(1, outcome.Failed);
        Assert.Empty(provider.Requests);   // NEITHER arm dispatched — not one, not a partial pair

        using var db = h.Open();
        Assert.Empty(db.JournalEntries.ToList());
        var job = Assert.Single(db.Jobs.ToList());
        Assert.Contains("Both arms abstain", job.ErrorJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSeatBudgetIsAttributedPerSeat_NotFromTheDailyLog()
    {
        // llm_budget_log is one row per DAY across every seat and task, so it cannot answer a per-seat
        // question. The attribution reads analysis_cache, which carries the task and the cost of each
        // call — a read of what was actually spent rather than a parallel tally that could drift.
        using var h = Harness(new RecordingProvider());
        using (var db = h.Open())
        {
            db.AnalysisCache.Add(new AnalysisCacheRow
            {
                PromptHash = "h1", Model = "m", AsOf = "2026-08-05", Task = AnalysisTaskNames.Hypotheses,
                OutputJson = "{}", CostUsd = 1.50,
            });
            // A regime brief is the MARKET seat's spend, not the researcher's — counting it here would
            // exhaust the researcher's budget on calls it never made.
            db.AnalysisCache.Add(new AnalysisCacheRow
            {
                PromptHash = "h2", Model = "m", AsOf = "2026-08-06", Task = AnalysisTaskNames.RegimeBrief,
                OutputJson = "{}", CostUsd = 9.00,
            });
            // …and a previous month is a previous budget.
            db.AnalysisCache.Add(new AnalysisCacheRow
            {
                PromptHash = "h3", Model = "m", AsOf = "2026-07-31", Task = AnalysisTaskNames.Hypotheses,
                OutputJson = "{}", CostUsd = 4.00,
            });
            db.SaveChanges();
        }

        using var read = h.Open();
        var state = new ResearcherSeatBudget(read, new AiOptions
        {
            Researcher = new ResearcherSeatOptions { MonthlyBudgetUsd = 5.0m },
        }).Assess("2026-08-07", estimatedArmUsd: 0.25m);

        Assert.Equal(1.50m, state.SpentUsd);
        Assert.Equal(0.50m, state.EstimatedPairUsd);
        Assert.True(state.PairFits);
        Assert.Equal(3.50m, state.RemainingUsd);
    }

    /// <summary>Records every request so the arm difference can be asserted on what was actually sent,
    /// not on what the composition intended to send.</summary>
    private sealed class RecordingProvider : IAnalysisProvider
    {
        public List<AnalysisRequest> Requests { get; } = [];

        public Task<IReadOnlyList<AnalysisResult>> RunBatchAsync(
            IReadOnlyList<AnalysisRequest> requests, CancellationToken ct = default)
        {
            Requests.AddRange(requests);
            return Task.FromResult<IReadOnlyList<AnalysisResult>>(
                [.. requests.Select(r => new AnalysisResult(
                    r.CustomId, AnalysisOutcome.Succeeded, "proposal", TokenUsage.Zero, "m"))]);
        }

        public Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AnalysisResult(
                request.CustomId, AnalysisOutcome.Succeeded, "proposal", TokenUsage.Zero, "m"));
        }
    }
}
