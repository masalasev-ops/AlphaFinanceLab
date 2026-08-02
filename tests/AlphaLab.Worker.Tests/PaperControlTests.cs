using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Ai;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The D113 paper control through the REAL pack path (rewired at v1.9.70, finding 330).
///
/// **Every assertion here is about a way the control could silently stop being a control** — two identical
/// arms, two different floors, an unblinded placebo, or one arm without the other. None of those would
/// throw; each would produce a margin series that looks fine and means nothing. The v1.9.67 versions of
/// these tests asserted against a stub prompt line that both violated D113 ("differ ONLY in the digest
/// field") and unblinded the control (finding 331) — they now assert against the persisted packs, which is
/// the record the comparison will actually be read from.
/// </summary>
public class PaperControlTests
{
    private const string AnchorAsOf = "2026-07-31";
    private const string AnchorWatermark = "2026-07-31T22:00:00Z";

    private static PipelineHarness Harness(IAnalysisProvider provider, decimal monthlyBudgetUsd = 5.0m) =>
        new(configure: s =>
        {
            s.AddScoped(_ => provider);
            s.AddSingleton(new AiOptions
            {
                Researcher = new ResearcherSeatOptions { MonthlyBudgetUsd = monthlyBudgetUsd },
            });
            s.AddSingleton(new ResearchOptions());
            s.AddSingleton(new SignalLibraryOptions());
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

    /// <summary>The pack anchor: a committed forward run. Without one the executor fails closed —
    /// asserted by its own test below, and a precondition for every other test here.</summary>
    private static void SeedRun(PipelineHarness h)
    {
        using var db = h.Open();
        db.Runs.Add(new RunRow
        {
            AsOf = AnchorAsOf, RunKind = "live", Watermark = AnchorWatermark,
            StartedAt = "t", Status = "ok",
        });
        db.SaveChanges();
    }

    /// <summary>
    /// A non-empty signal library, so the digest has content for the placebo to shuffle. Two signals with
    /// clearly different mean rank-ICs: with an EMPTY library the placebo shuffles nothing and the two
    /// packs are legitimately byte-identical — a placebo draw that coincides with truth, not a defect —
    /// so any test asserting the arms DIFFER must seed this first.
    /// </summary>
    private static void SeedSignals(PipelineHarness h)
    {
        using var db = h.Open();
        foreach (var (id, ic) in new[] { ("sig-a", 0.05), ("sig-b", -0.04) })
        {
            db.Signals.Add(new SignalRow
            {
                SignalId = id, Family = "momentum", ConfigJson = "{}", CodeVersion = "v1",
                RegisteredOn = "2026-06-01",
            });
            for (var d = 1; d <= 10; d++)
            {
                foreach (var horizon in new[] { 21, 63 })
                {
                    db.SignalIc.Add(new SignalIcRow
                    {
                        SignalId = id, AsOf = $"2026-07-{d:00}", HorizonDays = horizon,
                        RankIc = ic + d * 0.001, N = 90,
                    });
                }
            }
        }
        db.SaveChanges();
    }

    /// <summary>A σ estimate so the arena has a computable floor to stamp on both journal rows.</summary>
    private static void SeedSigma(PipelineHarness h)
    {
        using var db = h.Open();
        db.PowerReports.Add(new PowerReportRow
        {
            AsOf = "2026-07-30", StrategyA = "cand:a", StrategyB = "buyhold:cw", TDays = 500,
            SigmaLr = 0.0008, NwLag = 21, MdeAnn = 0.03, ObservedGapAnn = 0.0,
            Verdict = "TooEarly", RunKind = "replay",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task D113_OneJobRun_WritesBothArms_WithTheFullArtefactChain()
    {
        var provider = new RecordingProvider();
        using var h = Harness(provider);
        SeedRun(h);
        SeedSigma(h);
        SeedSignals(h);
        QueueProposal(h);

        var outcome = await h.RunJobDrainAsync();
        Assert.Equal(1, outcome.Done);

        using var db = h.Open();
        var jobId = db.Jobs.Single().JobId;

        // Artefact (a): one persisted pack per arm, anchored to the RUN's (as_of, watermark) — never the
        // wall clock — under the D114 subject grammar.
        var packs = db.AiContextPacks.OrderBy(p => p.PackId).ToList();
        Assert.Equal(2, packs.Count);
        Assert.All(packs, p => Assert.Equal(AiSeat.Researcher, p.Seat));
        Assert.All(packs, p => Assert.Equal(AnchorAsOf, p.AsOf));
        Assert.All(packs, p => Assert.Equal(AnchorWatermark, p.Watermark));
        var tPack = Assert.Single(packs, p => p.StrategyId == $"job:{jobId}#treatment");
        var cPack = Assert.Single(packs, p => p.StrategyId == $"job:{jobId}#control");
        Assert.NotEqual(tPack.PackHash, cPack.PackHash);   // the digest DIFFERS — not a duplicate

        // Artefacts (b)+(d): one decision row per arm, PackHash tying each to the exact pack seen, and
        // SamplingJson carrying the arm + seed (the ONLY place the seam mode is recorded — see blindness).
        var decisions = db.AiDecisions.OrderBy(d => d.DecisionId).ToList();
        Assert.Equal(2, decisions.Count);
        var tDec = Assert.Single(decisions, d => d.StrategyId == tPack.StrategyId);
        var cDec = Assert.Single(decisions, d => d.StrategyId == cPack.StrategyId);
        Assert.Equal(tPack.PackHash, tDec.PackHash);
        Assert.Equal(cPack.PackHash, cDec.PackHash);
        Assert.All(decisions, d => Assert.Equal(ResearchJobExecutor.PromptVersion, d.PromptVersion));
        Assert.Contains("\"seam\":\"on\"", tDec.SamplingJson!, StringComparison.Ordinal);
        Assert.Contains("\"seam\":\"placebo\"", cDec.SamplingJson!, StringComparison.Ordinal);

        // Artefact (c): each decision records WHICH draft it became.
        var entries = db.JournalEntries.OrderBy(e => e.EntryId).ToList();
        Assert.Equal(2, entries.Count);
        var tEntry = Assert.Single(entries, e => e.Title.Contains("[treatment]", StringComparison.Ordinal));
        var cEntry = Assert.Single(entries, e => e.Title.Contains("[control]", StringComparison.Ordinal));
        Assert.Contains($"\"journal_entry_id\":{tEntry.EntryId}", tDec.AppliedJson!, StringComparison.Ordinal);
        Assert.Contains($"\"journal_entry_id\":{cEntry.EntryId}", cDec.AppliedJson!, StringComparison.Ordinal);

        // The journal pair: same CURRENT floor read once (an admission between the arms would move it by
        // one Bonferroni step), same prior, both UNLOCKED, and the paper control costs zero trials.
        Assert.All(entries, e => Assert.NotNull(e.DetectabilityFloorAnn));
        Assert.Equal(tEntry.DetectabilityFloorAnn, cEntry.DetectabilityFloorAnn);
        Assert.All(entries, e => Assert.Equal(0.35, e.PriorProb));
        Assert.All(entries, e => Assert.False(e.Locked));
        Assert.Empty(db.TrialsRegistry.ToList());
        Assert.Empty(db.Strategies.ToList());
    }

    [Fact]
    public async Task D113_TheArmsDifferOnlyInTheDigestField_AndThePlaceboIsBlind()
    {
        // The arm difference is the whole content of the decision, asserted on the PERSISTED PACKS — the
        // record the margin comparison will actually be read from — not on prompt text.
        var provider = new RecordingProvider();
        using var h = Harness(provider);
        SeedRun(h);
        SeedSigma(h);
        SeedSignals(h);
        QueueProposal(h);

        await h.RunJobDrainAsync();

        using var db = h.Open();
        var jobId = db.Jobs.Single().JobId;
        var tPack = db.AiContextPacks.Single(p => p.StrategyId == $"job:{jobId}#treatment");
        var cPack = db.AiContextPacks.Single(p => p.StrategyId == $"job:{jobId}#control");

        using var tDoc = JsonDocument.Parse(tPack.PackJson);
        using var cDoc = JsonDocument.Parse(cPack.PackJson);
        var tFields = tDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
        var cFields = cDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());

        // Same field SET (the placebo holds shape), every COMMON field byte-equal, and the digest is the
        // one permitted difference. Anything else differing would be an alternative explanation for a
        // measured margin difference — which is what a control exists to eliminate.
        Assert.Equal(tFields.Keys.Order(StringComparer.Ordinal), cFields.Keys.Order(StringComparer.Ordinal));
        foreach (var name in tFields.Keys.Where(k => k != PackWhitelist.SignalDigest))
        {
            Assert.Equal(tFields[name], cFields[name]);
        }

        // D116 (cp-1.1): BOTH ends of the detectability band are present and COMMON. Named explicitly
        // rather than left to the loop above, which would pass just as happily if the fields vanished from
        // both packs — and a pack carrying only the floor is exactly the one-anchor state finding 337
        // found. The recipe id is asserted with them: an eighth field under the old id would make the
        // series unattributable, which is the one job `recipe_version` has.
        Assert.Contains(PackWhitelist.DetectabilityFloorAnn, tFields.Keys);
        Assert.Contains(PackWhitelist.DetectabilityCeilingAnn, tFields.Keys);
        Assert.Equal(
            tFields[PackWhitelist.DetectabilityCeilingAnn], cFields[PackWhitelist.DetectabilityCeilingAnn]);
        Assert.Equal("cp-1.1", tPack.RecipeVersion);
        Assert.Equal("cp-1.1", cPack.RecipeVersion);

        // …and with a non-empty library the digest DOES differ (this seed's permutation is non-identity;
        // everything is deterministic, so this is a stable assertion, not a probabilistic one).
        Assert.NotEqual(tFields[PackWhitelist.SignalDigest], cFields[PackWhitelist.SignalDigest]);

        // Token-count equality: a difference could otherwise be an artefact of prompt length (D113).
        Assert.Equal(tPack.TokenEstimate, cPack.TokenEstimate);

        // BLINDNESS (D114, finding 331): the prompt NEVER declares the arm. A control that is told its
        // evidence is fake is not a control; the v1.9.67 code emitted "Evidence-prior seam: placebo" into
        // L2 and the old version of this test asserted that line's presence. Arm identity lives only in
        // the records (subject, title, SamplingJson) — never in anything the model reads.
        Assert.Equal(2, provider.Requests.Count);
        foreach (var request in provider.Requests)
        {
            foreach (var marker in new[] { "seam", "placebo", "treatment", "control" })
            {
                Assert.DoesNotContain(marker, request.Prompt.StaticInstructions, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(marker, request.Prompt.Fresh, StringComparison.OrdinalIgnoreCase);
            }
        }

        // And the L0/L1 prefix is byte-identical across arms — the cached prefix must not move, or the
        // control is cheaper than the treatment for a reason unrelated to the seam.
        Assert.Equal(provider.Requests[0].Prompt.StaticInstructions, provider.Requests[1].Prompt.StaticInstructions);
        Assert.Equal(provider.Requests[0].Prompt.LessonSet, provider.Requests[1].Prompt.LessonSet);
    }

    [Fact]
    public async Task FX_PackNoLeak_RealPath_APostAnchorClosureCannotEnterThePack()
    {
        // The leak that matters on THIS path: a hypothesis closed AFTER the anchor must not appear in
        // closed_outcomes, however tempting the mutable outcome column makes it. The read is bounded by
        // the OUTCOME ENTRY's created_on — the recorded closure act.
        var provider = new RecordingProvider();
        using var h = Harness(provider);
        SeedRun(h);
        SeedSigma(h);
        using (var db = h.Open())
        {
            // Closed BEFORE the anchor — admissible.
            db.JournalEntries.Add(new JournalEntryRow
            {
                EntryId = 0, CreatedOn = "2026-06-01", Kind = "hypothesis", Title = "old-claim", BodyMd = "b",
                Metric = "alpha", EvidenceWindowDays = 21, Locked = true, Outcome = "refuted",
            });
            db.SaveChanges();
            var oldId = db.JournalEntries.Single(e => e.Title == "old-claim").EntryId;
            db.JournalEntries.Add(new JournalEntryRow
            {
                CreatedOn = "2026-07-01", Kind = "outcome", Title = "closure of old-claim", BodyMd = "b",
                LinkedEntryId = oldId,
            });

            // Closed AFTER the anchor — the column says closed, but the closure act post-dates the pack.
            db.JournalEntries.Add(new JournalEntryRow
            {
                CreatedOn = "2026-07-15", Kind = "hypothesis", Title = "late-claim", BodyMd = "b",
                Metric = "alpha", EvidenceWindowDays = 21, Locked = true, Outcome = "confirmed",
            });
            db.SaveChanges();
            var lateId = db.JournalEntries.Single(e => e.Title == "late-claim").EntryId;
            db.JournalEntries.Add(new JournalEntryRow
            {
                CreatedOn = "2026-08-15", Kind = "outcome", Title = "closure of late-claim", BodyMd = "b",
                LinkedEntryId = lateId,
            });
            db.SaveChanges();
        }
        QueueProposal(h);

        await h.RunJobDrainAsync();

        using var check = h.Open();
        var pack = check.AiContextPacks.First();
        Assert.Contains("old-claim", pack.PackJson, StringComparison.Ordinal);
        Assert.DoesNotContain("late-claim", pack.PackJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D113_PacksAreDeterministic_TwoIdenticalArenasProduceIdenticalHashes()
    {
        // FX-PackWatermark on the real path: the same store state and the same job id must yield the same
        // pack bytes — INCLUDING the placebo, whose shuffle is seeded from (asOf, jobId) rather than from
        // process randomness. An irreproducible control is not a control.
        static async Task<(string T, string C)> BuildOnce()
        {
            var provider = new RecordingProvider();
            using var h = Harness(provider);
            SeedRun(h);
            SeedSigma(h);
            SeedSignals(h);
            QueueProposal(h);
            await h.RunJobDrainAsync();
            using var db = h.Open();
            var jobId = db.Jobs.Single().JobId;
            return (
                db.AiContextPacks.Single(p => p.StrategyId == $"job:{jobId}#treatment").PackHash,
                db.AiContextPacks.Single(p => p.StrategyId == $"job:{jobId}#control").PackHash);
        }

        var first = await BuildOnce();
        var second = await BuildOnce();

        Assert.Equal(first.T, second.T);
        Assert.Equal(first.C, second.C);
    }

    [Fact]
    public async Task NoCommittedRun_FailsClosed_WritingNothingAnywhere()
    {
        // An arena that has never committed a session has no evidence to pack. Failing closed here is
        // rule 10; the alternative — a wall-clock-stamped pack over no data — would be a record claiming
        // knowledge that was never there.
        var provider = new RecordingProvider();
        using var h = Harness(provider);
        QueueProposal(h);   // deliberately NO SeedRun

        var outcome = await h.RunJobDrainAsync();

        Assert.Equal(1, outcome.Failed);
        Assert.Empty(provider.Requests);
        using var db = h.Open();
        Assert.Empty(db.AiContextPacks.ToList());
        Assert.Empty(db.AiDecisions.ToList());
        Assert.Empty(db.JournalEntries.ToList());
        var job = Assert.Single(db.Jobs.ToList());
        Assert.Contains("no committed forward run", job.ErrorJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FX_BudgetAbstain_AnExhaustedMonthAbstainsBOTHArms_NeverOne()
    {
        // The sharper risk is not the doubling but the UNPAIRING: a treatment proposal with no control is
        // an unpaired observation silently entering the margin series — worse than no observation,
        // because it looks like one.
        var provider = new RecordingProvider();
        using var h = Harness(provider, monthlyBudgetUsd: 0.10m);   // less than one pair
        SeedRun(h);
        SeedSigma(h);
        QueueProposal(h);

        var outcome = await h.RunJobDrainAsync();

        Assert.Equal(1, outcome.Failed);
        Assert.Empty(provider.Requests);   // NEITHER arm dispatched — not one, not a partial pair

        using var db = h.Open();
        Assert.Empty(db.JournalEntries.ToList());
        Assert.Empty(db.AiDecisions.ToList());
        var job = Assert.Single(db.Jobs.ToList());
        Assert.Contains("Both arms abstain", job.ErrorJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AOneArmedResponse_FailsTheJob_AndWritesNoJournalRows()
    {
        // The pairing constraint from the RESPONSE side: if only one arm's call succeeds, writing its
        // draft alone would emit the same unpaired observation the budget check guards against.
        var provider = new RecordingProvider { FailArm = "control" };
        using var h = Harness(provider);
        SeedRun(h);
        SeedSigma(h);
        QueueProposal(h);

        var outcome = await h.RunJobDrainAsync();

        Assert.Equal(1, outcome.Failed);
        using var db = h.Open();
        Assert.Empty(db.JournalEntries.ToList());   // not even the successful treatment arm
        Assert.Empty(db.AiDecisions.ToList());
        var job = Assert.Single(db.Jobs.ToList());
        Assert.Contains("both or neither", job.ErrorJson!, StringComparison.Ordinal);
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

    /// <summary>Records every request so the blindness and prefix assertions run against what was actually
    /// sent, not what the composition intended. <see cref="FailArm"/> makes exactly one arm's result
    /// unusable, for the one-armed-response test.</summary>
    private sealed class RecordingProvider : IAnalysisProvider
    {
        public List<AnalysisRequest> Requests { get; } = [];

        public string? FailArm { get; init; }

        public Task<IReadOnlyList<AnalysisResult>> RunBatchAsync(
            IReadOnlyList<AnalysisRequest> requests, CancellationToken ct = default)
        {
            Requests.AddRange(requests);
            return Task.FromResult<IReadOnlyList<AnalysisResult>>(
                [.. requests.Select(r =>
                    FailArm is not null && r.CustomId.EndsWith(":" + FailArm, StringComparison.Ordinal)
                        ? new AnalysisResult(r.CustomId, AnalysisOutcome.Unavailable, "", TokenUsage.Zero, "m", "boom")
                        : new AnalysisResult(r.CustomId, AnalysisOutcome.Succeeded, "proposal", TokenUsage.Zero, "m"))]);
        }

        public Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AnalysisResult(
                request.CustomId, AnalysisOutcome.Succeeded, "proposal", TokenUsage.Zero, "m"));
        }
    }
}
