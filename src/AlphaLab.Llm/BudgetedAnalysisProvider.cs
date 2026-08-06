using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm;

/// <summary>
/// The D24 budget and the FR-21 cache, as a decorator over any <see cref="IAnalysisProvider"/>.
///
/// **The order is the contract, and it is not arbitrary:**
/// <list type="number">
/// <item><b>Cache first.</b> A hit spends nothing and must not consume budget headroom it does not need
/// (<c>FR21_CacheHit_CostsZero</c>). Checking the budget first would let an exhausted day refuse a read it
/// was never going to pay for.</item>
/// <item><b>Budget second, BEFORE any token</b> (rule 13, D24). A pre-flight estimate decides; an
/// over-ceiling request is refused unspent and the day is stamped <c>degraded</c>.</item>
/// <item><b>Spend last</b>, and record what it actually cost — the API's reported usage, never the
/// estimate.</item>
/// </list>
///
/// A decorator rather than logic inside the Anthropic provider so the rail is testable with no transport,
/// and so it applies unchanged to any future provider (the D25 local-model contestant included).
/// </summary>
public sealed class BudgetedAnalysisProvider(
    IAnalysisProvider inner,
    IAnalysisCache cache,
    ILlmBudgetLedger ledger,
    LlmOptions llm,
    Func<string> asOf,
    AnthropicProviderOptions? options = null) : IAnalysisProvider
{
    private readonly AnthropicProviderOptions _opts = options ?? new AnthropicProviderOptions();

    public async Task<IReadOnlyList<AnalysisResult>> RunBatchAsync(
        IReadOnlyList<AnalysisRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) return [];

        var day = asOf();
        var results = new Dictionary<string, AnalysisResult>(StringComparer.Ordinal);
        var toSpend = new List<AnalysisRequest>();

        // ---- 1. Cache. Served rows cost nothing and never reach the budget.
        foreach (var r in requests)
        {
            var model = llm.ModelFor(r.Task.Wire());
            var hit = await cache
                .TryGetAsync(AnthropicWire.PromptHash(r.Prompt), model, day, ct).ConfigureAwait(false);

            if (hit is not null)
            {
                // TokenUsage.Zero, not the original usage: this call spent nothing. The original cost is
                // already recorded against the day that paid it, and re-reporting it here would
                // double-count the same tokens in llm_budget_log.
                results[r.CustomId] = new AnalysisResult(
                    r.CustomId, AnalysisOutcome.CacheHit, hit.RawOutput, TokenUsage.Zero, model);
            }
            else
            {
                toSpend.Add(r);
            }
        }

        // ---- 2. Budget, pre-flight. Whatever the ceiling refuses is degraded, unspent.
        var state = await ledger.GetAsync(day, ct).ConfigureAwait(false);
        var admitted = new List<AnalysisRequest>();
        var refused = 0;

        foreach (var r in toSpend)
        {
            var model = llm.ModelFor(r.Task.Wire());
            // D130/finding 380: the estimate's output term is the task's PRE-REGISTERED expected value,
            // not the API ceiling — the ceiling (8192) as the output term made the guard refuse calls the
            // budget could afford (a lockout, since output dominates cost). Ceiling remains the fallback.
            var expectedOut = llm.ExpectedOutputTokensFor(r.Task.Wire(), _opts.MaxOutputTokens);
            var estimate = CostModel.Estimate(
                r.Prompt, expectedOut, llm.PricingFor(model), llm.BatchDiscountMultiplier,
                llm.UseBatchesApiForScheduled);
            var estimateTokens = CostModel.EstimateTokenCount(r.Prompt, expectedOut);

            if (WouldExceed(state, estimate, estimateTokens, admitted.Count))
            {
                results[r.CustomId] = new AnalysisResult(
                    r.CustomId, AnalysisOutcome.BudgetExhausted, "", TokenUsage.Zero, model,
                    "D24 daily budget exhausted — read degraded, no tokens spent");
                refused++;
            }
            else
            {
                admitted.Add(r);
            }
        }

        // ---- 3. Spend.
        if (admitted.Count > 0)
        {
            foreach (var res in await inner.RunBatchAsync(admitted, ct).ConfigureAwait(false))
            {
                results[res.CustomId] = res;
                if (res.Outcome is AnalysisOutcome.Succeeded)
                {
                    var req = admitted.First(a => a.CustomId == res.CustomId);
                    await cache.PutAsync(
                        AnthropicWire.PromptHash(req.Prompt), res.ModelVersion, day, req.Task,
                        res.RawOutput, res.Usage, ct).ConfigureAwait(false);
                }
            }
        }

        await RecordAsync(day, results.Values, refused, ct).ConfigureAwait(false);

        // Rebuilt in request order — the caller's ordering is never the dictionary's.
        return [.. requests.Select(r => results[r.CustomId])];
    }

    public async Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var day = asOf();
        var model = llm.ModelFor(request.Task.Wire());
        var hash = AnthropicWire.PromptHash(request.Prompt);

        var hit = await cache.TryGetAsync(hash, model, day, ct).ConfigureAwait(false);
        if (hit is not null)
        {
            return new AnalysisResult(
                request.CustomId, AnalysisOutcome.CacheHit, hit.RawOutput, TokenUsage.Zero, model);
        }

        var state = await ledger.GetAsync(day, ct).ConfigureAwait(false);
        var expectedOut = llm.ExpectedOutputTokensFor(request.Task.Wire(), _opts.MaxOutputTokens);
        var estimate = CostModel.Estimate(
            request.Prompt, expectedOut, llm.PricingFor(model), llm.BatchDiscountMultiplier, batched: false);
        var estimateTokens = CostModel.EstimateTokenCount(request.Prompt, expectedOut);

        if (WouldExceed(state, estimate, estimateTokens, 0))
        {
            await ledger.RecordAsync(day, 0, TokenUsage.Zero, degraded: true, "budget exhausted", ct)
                .ConfigureAwait(false);
            return new AnalysisResult(
                request.CustomId, AnalysisOutcome.BudgetExhausted, "", TokenUsage.Zero, model,
                "D24 daily budget exhausted — read degraded, no tokens spent");
        }

        var result = await inner.RunAsync(request, ct).ConfigureAwait(false);
        if (result.Outcome is AnalysisOutcome.Succeeded)
        {
            await cache.PutAsync(hash, result.ModelVersion, day, request.Task, result.RawOutput, result.Usage, ct)
                .ConfigureAwait(false);
        }
        await RecordAsync(day, [result], 0, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Would admitting one more call at <paramref name="estimate"/> (costing
    /// <paramref name="estimateTokens"/> tokens) cross any of the three D24 ceilings? A zero ceiling means
    /// that dimension is not enforced.</summary>
    private bool WouldExceed(BudgetState state, decimal estimate, int estimateTokens, int alreadyAdmitted)
    {
        var b = llm.DailyBudget;
        if (b.MaxCalls > 0 && state.Calls + alreadyAdmitted + 1 > b.MaxCalls) return true;
        if (b.MaxCostUsd > 0m && state.CostUsd + estimate > b.MaxCostUsd) return true;
        // MaxTokens (finding 320), aligned to the cost guard's PRE-FLIGHT shape (finding 382, v1.9.94).
        // It was `state.Tokens >= cap` — backward-looking, where the cost dimension above is
        // `state + estimate > cap` — so the token ceiling admitted ONE call past its limit before
        // refusing. The estimate's token count is the same quantity CostModel.Estimate prices: the
        // prompt's input tokens plus the task's expected output (D130's pre-registered seed, never the
        // API ceiling). The recorded spend below still uses the API's actual counts.
        if (b.MaxTokens > 0 && state.Tokens + estimateTokens > b.MaxTokens) return true;
        return false;
    }

    private async Task RecordAsync(string day, IEnumerable<AnalysisResult> results, int refused, CancellationToken ct)
    {
        var calls = 0;
        var totals = TokenUsage.Zero;
        foreach (var r in results)
        {
            if (r.Outcome is AnalysisOutcome.CacheHit or AnalysisOutcome.BudgetExhausted) continue;
            calls++;
            totals = new TokenUsage(
                totals.InputTokens + r.Usage.InputTokens,
                totals.OutputTokens + r.Usage.OutputTokens,
                totals.CacheReadTokens + r.Usage.CacheReadTokens,
                totals.CacheWriteTokens + r.Usage.CacheWriteTokens,
                totals.CostUsd + r.Usage.CostUsd);
        }

        // A day on which nothing was spent and nothing was refused needs no row — silence is accurate.
        if (calls == 0 && refused == 0) return;

        await ledger.RecordAsync(
            day, calls, totals, degraded: refused > 0,
            refused > 0 ? $"{refused} request(s) degraded — D24 ceiling" : null, ct).ConfigureAwait(false);
    }
}
