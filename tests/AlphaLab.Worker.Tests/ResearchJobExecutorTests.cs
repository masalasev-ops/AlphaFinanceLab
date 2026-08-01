using AlphaLab.Core.Llm;
using AlphaLab.Data.Entities;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The researcher seat's Worker half (FR-23, D82): what the API's 202 actually becomes.
///
/// The API tests assert the CONTRACT (status codes, the refusal count); these assert the OUTCOME — that a
/// proposal lands as an unlocked journal entry and nothing else. Neither half can see the other's failure
/// mode, which is why both exist.
/// </summary>
public class ResearchJobExecutorTests
{
    private static PipelineHarness Harness(IAnalysisProvider provider) =>
        new(configure: s =>
        {
            s.AddScoped(_ => provider);
            foreach (var kind in ResearchJobExecutor.Kinds)
            {
                var k = kind;
                s.AddSingleton<IJobExecutor>(sp => new ResearchJobExecutor(
                    k,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ResearchJobExecutor>>()));
            }
        });

    private static long Queue(PipelineHarness h, string kind, string requestJson)
    {
        using var db = h.Open();
        var job = new JobRow { Kind = kind, Status = "queued", SubmittedAt = "2026-08-01T10:00:00Z", RequestJson = requestJson };
        db.Jobs.Add(job);
        db.SaveChanges();
        return job.JobId;
    }

    [Fact]
    public async Task FR23_AnAcceptedProposal_LandsUnlocked_LinkedToItsParent()
    {
        using var h = Harness(new StubProvider("## Hypothesis 1\nClaim: momentum decays faster after gaps."));
        Queue(h, "analysis_hypotheses", """{"ParentEntryId":41,"Topic":"gap behaviour"}""");

        var outcome = await h.RunJobDrainAsync();
        Assert.Equal(1, outcome.Done);

        using var db = h.Open();
        var entry = Assert.Single(db.JournalEntries.ToList());
        Assert.Equal("hypothesis", entry.Kind);

        // THE invariant of this seat: the AI proposes, the operator pre-registers (rule 30). A locked row
        // here would mean the seat pre-registered its own claim, and a pre-registration made by the same
        // party that makes the claim is not a commitment — it is a label.
        Assert.False(entry.Locked);
        Assert.Null(entry.Outcome);
        Assert.Equal(41, entry.LinkedEntryId);
        Assert.Contains("momentum decays", entry.BodyMd, StringComparison.Ordinal);

        // And nothing else moved: no candidate, no trial. A proposal is a sentence, not an admission.
        Assert.Empty(db.Strategies.ToList());
        Assert.Empty(db.TrialsRegistry.ToList());
    }

    [Fact]
    public async Task BriefAndSkeptic_LandAsTheirOwnKinds_LinkedToTheStrategyTheyAreAbout()
    {
        using var h = Harness(new StubProvider("prose"));
        Queue(h, "analysis_brief", """{"StrategyId":"cand:a"}""");
        Queue(h, "analysis_skeptic", """{"StrategyId":"cand:a"}""");

        var outcome = await h.RunJobDrainAsync();
        Assert.Equal(2, outcome.Done);

        using var db = h.Open();
        var entries = db.JournalEntries.ToList();
        Assert.Equal(2, entries.Count);

        // D52: a review not linked to its subject is an opinion about nothing. Both carry strategy_id.
        Assert.All(entries, e => Assert.Equal("cand:a", e.StrategyId));
        Assert.Contains(entries, e => e.Kind == "decision_note");
        Assert.Contains(entries, e => e.Kind == "skeptic_review");
        Assert.All(entries, e => Assert.False(e.Locked));
    }

    [Fact]
    public async Task AnUnavailableModel_FailsTheJobClosed_AndWritesNoEntry()
    {
        // Rule 10. The alternative — an empty journal entry — would put an unattributed blank into the
        // D110 proposal stream, which reads as the researcher having had nothing to say.
        using var h = Harness(new StubProvider("", AnalysisOutcome.BudgetExhausted, "day ceiling reached"));
        Queue(h, "analysis_hypotheses", """{"ParentFinding":"f-311"}""");

        var outcome = await h.RunJobDrainAsync();

        Assert.Equal(1, outcome.Failed);
        using var db = h.Open();
        Assert.Empty(db.JournalEntries.ToList());
        var job = Assert.Single(db.Jobs.ToList());
        Assert.Equal("failed", job.Status);
        Assert.Contains("BudgetExhausted", job.ErrorJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProposalInstructions_NameAllFourPreDeclaredFields()
    {
        // A hypothesis missing any of the four cannot be pre-registered without the operator inventing the
        // missing part — at which point the claim being tested is no longer the one the seat made. Asserted
        // on the frozen prompt because that is where the requirement is actually enforced.
        foreach (var required in new[] { "claim", "metric", "evidence window", "expected annualized effect" })
        {
            Assert.Contains(required, ResearchJobExecutor.HypothesesInstructions, StringComparison.OrdinalIgnoreCase);
        }

        // Rule 30 again, stated to the model rather than only enforced after it.
        Assert.Contains("proposing, not deciding", ResearchJobExecutor.HypothesesInstructions, StringComparison.OrdinalIgnoreCase);

        // The skeptic must not hedge into balance — a review that finds the claim reasonable adds nothing
        // the claim did not already assert.
        Assert.Contains("Do not hedge into balance", ResearchJobExecutor.SkepticInstructions, StringComparison.Ordinal);

        // Rule 28 (no resurrected sentiment number) applies to every seat, not only the regime brief.
        Assert.Contains("No trading recommendations", ResearchJobExecutor.BriefInstructions, StringComparison.Ordinal);
        Assert.Contains("numeric score", ResearchJobExecutor.BriefInstructions, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubProvider(
        string output,
        AnalysisOutcome outcome = AnalysisOutcome.Succeeded,
        string? detail = null) : IAnalysisProvider
    {
        public Task<IReadOnlyList<AnalysisResult>> RunBatchAsync(
            IReadOnlyList<AnalysisRequest> requests, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AnalysisResult>>(
                [.. requests.Select(r => new AnalysisResult(r.CustomId, outcome, output, TokenUsage.Zero, "m", detail))]);

        public Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken ct = default)
            => Task.FromResult(new AnalysisResult(request.CustomId, outcome, output, TokenUsage.Zero, "m", detail));
    }
}
