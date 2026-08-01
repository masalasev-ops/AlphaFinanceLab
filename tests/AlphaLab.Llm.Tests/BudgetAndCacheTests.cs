using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm.Tests;

/// <summary>
/// FR-21 caching and the D24 budget rail (TEST_PLAN §6). Every test runs the real provider stack against
/// the fake transport — the mocked-provider path CI uses.
/// </summary>
public class BudgetAndCacheTests
{
    private const string Day = "2026-08-03";

    private static (BudgetedAnalysisProvider Provider, FakeTransport Transport, FakeCache Cache, FakeLedger Ledger)
        Build(LlmOptions? llm = null)
    {
        llm ??= TestOptions.Llm();
        var transport = new FakeTransport();
        var cache = new FakeCache();
        var ledger = new FakeLedger();
        var inner = new AnthropicAnalysisProvider(
            transport, llm, new AnthropicProviderOptions { PollInterval = TimeSpan.Zero },
            delay: (_, _) => Task.CompletedTask);
        return (new BudgetedAnalysisProvider(inner, cache, ledger, llm, () => Day), transport, cache, ledger);
    }

    private static void ScriptBatch(FakeTransport t, params (string Id, string Text)[] results)
    {
        t.EnqueuePost("""{"id":"msgbatch_1","processing_status":"in_progress"}""");
        t.EnqueueGet("""{"id":"msgbatch_1","processing_status":"ended"}""");
        const string template =
            """{"custom_id":"ID","result":{"type":"succeeded","message":{"model":"claude-opus-5","stop_reason":"end_turn","content":[{"type":"text","text":"TEXT"}],"usage":{"input_tokens":100,"output_tokens":50}}}}""";
        var lines = results.Select(r => template
            .Replace("ID", r.Id, StringComparison.Ordinal)
            .Replace("TEXT", r.Text, StringComparison.Ordinal));
        t.EnqueueGet(string.Join("\n", lines));
    }

    [Fact]
    public async Task FR21_CacheHit_CostsZero()
    {
        var (provider, transport, cache, ledger) = Build();
        var request = TestOptions.Request("r1");

        // Seed the cache with the exact key the provider will compute — same prompt, same model, same day.
        cache.Seed(AnthropicWire.PromptHash(request.Prompt), TestOptions.OpusModel, Day, "cached answer");

        var results = await provider.RunBatchAsync([request]);

        Assert.Equal(AnalysisOutcome.CacheHit, results[0].Outcome);
        Assert.Equal("cached answer", results[0].RawOutput);

        // "Spends nothing" asserted THREE ways, because the weak version of this test passes on a
        // provider that calls the API and then reports zero:
        Assert.Equal(0m, results[0].Usage.CostUsd);          // nothing reported
        Assert.Equal(0, transport.CallCount);                 // nothing reached the transport
        Assert.Empty(ledger.Records);                         // nothing charged to the day
    }

    [Fact]
    public async Task FR21_CacheIsKeyedOnModel_SoARepinMisses()
    {
        // A re-pin (v1.9.60) must MISS rather than serve an answer the current tier never produced.
        var (provider, transport, cache, _) = Build();
        var request = TestOptions.Request("r1");
        cache.Seed(AnthropicWire.PromptHash(request.Prompt), "claude-sonnet-4-6", Day, "answer from the old tier");
        ScriptBatch(transport, ("r1", "fresh answer"));

        var results = await provider.RunBatchAsync([request]);

        Assert.Equal(AnalysisOutcome.Succeeded, results[0].Outcome);
        Assert.Equal("fresh answer", results[0].RawOutput);
    }

    [Fact]
    public async Task Batch_ResultsAreCorrelatedByCustomId_NotByPosition()
    {
        // The API returns results in ANY order. This scripts them REVERSED: a provider that zips by
        // position passes every other test and silently mis-attributes every answer here.
        var (provider, transport, _, _) = Build();
        ScriptBatch(transport, ("r2", "SECOND"), ("r1", "FIRST"));

        var results = await provider.RunBatchAsync(
            [TestOptions.Request("r1"), TestOptions.Request("r2")]);

        Assert.Equal("r1", results[0].CustomId);
        Assert.Equal("FIRST", results[0].RawOutput);
        Assert.Equal("r2", results[1].CustomId);
        Assert.Equal("SECOND", results[1].RawOutput);
    }

    [Fact]
    public async Task Batch_MissingResultForACustomId_IsANoReadForThatIdOnly()
    {
        var (provider, transport, _, _) = Build();
        ScriptBatch(transport, ("r1", "ok"));   // r2 never comes back

        var results = await provider.RunBatchAsync(
            [TestOptions.Request("r1"), TestOptions.Request("r2")]);

        Assert.Equal(AnalysisOutcome.Succeeded, results[0].Outcome);
        Assert.Equal(AnalysisOutcome.Unavailable, results[1].Outcome);
    }

    [Fact]
    public async Task FX_BudgetRefusal_SpendsNothingAndStampsDegraded()
    {
        // A day already at the call ceiling. The request must be refused BEFORE any token is spent
        // (rule 13) — not attempted and then reconciled.
        var llm = TestOptions.Llm(maxCalls: 1);
        var (provider, transport, _, ledger) = Build(llm);
        ledger.Seed(Day, new BudgetState(Calls: 1, Tokens: 0, CostUsd: 0m));

        var results = await provider.RunBatchAsync([TestOptions.Request("r1")]);

        Assert.Equal(AnalysisOutcome.BudgetExhausted, results[0].Outcome);
        Assert.Equal(0, transport.CallCount);
        Assert.Equal(0m, results[0].Usage.CostUsd);

        // The day is stamped degraded, so an over-budget day is a recorded FACT rather than something
        // inferred later from a gap in analysis_cache.
        Assert.True(ledger.Records[^1].Degraded);
    }

    [Fact]
    public async Task Budget_CostCeilingRefusesBeforeSpending()
    {
        var llm = TestOptions.Llm(maxCost: 0.0000001m);
        var (provider, transport, _, _) = Build(llm);

        var results = await provider.RunBatchAsync([TestOptions.Request("r1")]);

        Assert.Equal(AnalysisOutcome.BudgetExhausted, results[0].Outcome);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Budget_TokenCeilingIsEnforced_Finding320()
    {
        // The tokens column pre-dated its ceiling; MaxTokens gives it an enforcer.
        var llm = TestOptions.Llm(maxTokens: 100);
        var (provider, transport, _, ledger) = Build(llm);
        ledger.Seed(Day, new BudgetState(Calls: 0, Tokens: 500, CostUsd: 0m));

        var results = await provider.RunBatchAsync([TestOptions.Request("r1")]);

        Assert.Equal(AnalysisOutcome.BudgetExhausted, results[0].Outcome);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Budget_PartialAdmission_SomeSpendSomeDegrade_NeverABlackout()
    {
        // The D24 contract is an ORDERED degradation, not a cut-off: an over-budget day still answers for
        // what fits. A blackout would be the wrong failure.
        var llm = TestOptions.Llm(maxCalls: 1);
        var (provider, transport, _, _) = Build(llm);
        ScriptBatch(transport, ("r1", "served"));

        var results = await provider.RunBatchAsync(
            [TestOptions.Request("r1"), TestOptions.Request("r2")]);

        Assert.Equal(AnalysisOutcome.Succeeded, results[0].Outcome);
        Assert.Equal(AnalysisOutcome.BudgetExhausted, results[1].Outcome);
    }

    [Fact]
    public async Task SucceededResult_IsCached_SoTheSecondReadIsFree()
    {
        var (provider, transport, cache, _) = Build();
        ScriptBatch(transport, ("r1", "answer"));

        await provider.RunBatchAsync([TestOptions.Request("r1")]);
        Assert.Equal(1, cache.PutCount);

        var callsAfterFirst = transport.CallCount;
        var second = await provider.RunBatchAsync([TestOptions.Request("r1")]);

        Assert.Equal(AnalysisOutcome.CacheHit, second[0].Outcome);
        Assert.Equal(callsAfterFirst, transport.CallCount);   // no new transport traffic
    }

    [Fact]
    public async Task RefusedResult_IsNotCached()
    {
        // A refusal is not an answer. Caching it would serve the refusal all day and make the outcome
        // look like a stable property of the prompt rather than of that one call.
        var (provider, transport, cache, _) = Build();
        transport.EnqueuePost("""{"id":"msgbatch_1","processing_status":"in_progress"}""");
        transport.EnqueueGet("""{"id":"msgbatch_1","processing_status":"ended"}""");
        transport.EnqueueGet(
            """{"custom_id":"r1","result":{"type":"succeeded","message":{"model":"claude-opus-5","stop_reason":"refusal","content":[],"usage":{"input_tokens":5,"output_tokens":0}}}}""");

        var results = await provider.RunBatchAsync([TestOptions.Request("r1")]);

        Assert.Equal(AnalysisOutcome.Refused, results[0].Outcome);
        Assert.Equal(0, cache.PutCount);
    }

    [Fact]
    public async Task TransportFailure_IsANoReadDay_NotAnException()
    {
        // A late or failed batch is a no-read day and never a blocker (D53 Stage 3). Forward-only makes
        // that safe: the read would only have informed subsequent days.
        var (provider, transport, _, _) = Build();
        transport.FailNextPost = true;

        var results = await provider.RunBatchAsync([TestOptions.Request("r1")]);

        Assert.Equal(AnalysisOutcome.Unavailable, results[0].Outcome);
    }

    [Fact]
    public async Task MockedMonth_OfDailyReads_StaysUnderBudget()
    {
        // The phase DoD's "a full month of daily reads lands under budget", as a unit test rather than an
        // estimate. One market-level read per trading day at ScopeLevel 1.
        var llm = TestOptions.Llm();
        var totalCost = 0m;

        for (var d = 1; d <= 21; d++)
        {
            var day = $"2026-09-{d:00}";
            var transport = new FakeTransport();
            var inner = new AnthropicAnalysisProvider(
                transport, llm, new AnthropicProviderOptions { PollInterval = TimeSpan.Zero },
                delay: (_, _) => Task.CompletedTask);
            var provider = new BudgetedAnalysisProvider(inner, new FakeCache(), new FakeLedger(), llm, () => day);
            ScriptBatch(transport, ("regime", "brief"));

            var r = await provider.RunBatchAsync([TestOptions.Request("regime", $"rows for {day}")]);
            Assert.Equal(AnalysisOutcome.Succeeded, r[0].Outcome);
            totalCost += r[0].Usage.CostUsd;
        }

        // 100 in + 50 out per day on the Opus tier at Batches half price. Asserted against the DAILY
        // ceiling × 21 rather than a hardcoded total, so a re-pin re-prices the test with the model.
        Assert.True(totalCost < llm.DailyBudget.MaxCostUsd * 21, $"month cost {totalCost:C4}");
        Assert.True(totalCost > 0m, "a month of real reads must cost something");
    }
}
