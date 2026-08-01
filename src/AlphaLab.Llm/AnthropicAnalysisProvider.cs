using System.Text.Json.Nodes;
using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm;

/// <summary>Tuning for the batch poll loop. Separate from <see cref="LlmOptions"/> because these are
/// operational timings, not economics.</summary>
public sealed class AnthropicProviderOptions
{
    /// <summary>Output ceiling per request. Sized with headroom because on the pinned tier
    /// <c>max_tokens</c> caps thinking AND response text together — a value sized for the answer alone
    /// truncates mid-thought.</summary>
    public int MaxOutputTokens { get; init; } = 8192;

    /// <summary>Gap between batch polls. Most batches end within an hour; the ceiling is 24 h.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to keep polling before giving up. Exceeding it is a **no-read day**, not a
    /// failure: forward-only (D16) makes a late read safe, because it would only have informed
    /// subsequent days anyway.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromHours(2);
}

/// <summary>
/// <see cref="IAnalysisProvider"/> over the Anthropic Message Batches API (FR-21, D46; INTEGRATIONS §5).
///
/// **Transport and wire only.** The <c>analysis_cache</c> lookup and the D24 budget live in
/// <see cref="BudgetedAnalysisProvider"/>, which decorates this — so "did we spend?" is decided in one
/// place and can be tested without a transport at all.
///
/// Holds no HTTP client: it talks to <see cref="IModelTransport"/>, which AlphaLab.Data satisfies over the
/// shared resilient client. That indirection is forced by the `ci.ps1` reference graph and is explained at
/// <see cref="IModelTransport"/>.
/// </summary>
public sealed class AnthropicAnalysisProvider(
    IModelTransport transport,
    LlmOptions llm,
    AnthropicProviderOptions? options = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset>? now = null) : IAnalysisProvider
{
    private readonly AnthropicProviderOptions _opts = options ?? new AnthropicProviderOptions();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public async Task<IReadOnlyList<AnalysisResult>> RunBatchAsync(
        IReadOnlyList<AnalysisRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) return [];

        // The synchronous escape hatch exists for local debugging only; it forfeits the D46 half price,
        // so it is never the scheduled path.
        if (!llm.UseBatchesApiForScheduled)
        {
            var one = new List<AnalysisResult>(requests.Count);
            foreach (var r in requests) one.Add(await RunAsync(r, ct).ConfigureAwait(false));
            return one;
        }

        var body = AnthropicWire.BuildBatchBody(
            requests, t => llm.ModelFor(t.Wire()), _opts.MaxOutputTokens, llm.PromptCacheStaticBlock);

        string batchId;
        try
        {
            batchId = AnthropicWire.ReadBatchId(
                await transport.PostJsonAsync(AnthropicWire.BatchesPath, body, ct).ConfigureAwait(false));
        }
        catch (ModelTransportException ex)
        {
            return Unavailable(requests, $"batch submit failed: {ex.Message}");
        }

        var deadline = _now() + _opts.PollTimeout;
        while (true)
        {
            string poll;
            try
            {
                poll = await transport.GetJsonAsync($"{AnthropicWire.BatchesPath}/{batchId}", ct).ConfigureAwait(false);
            }
            catch (ModelTransportException ex)
            {
                return Unavailable(requests, $"batch poll failed: {ex.Message}");
            }

            if (AnthropicWire.IsBatchEnded(poll)) break;

            if (_now() >= deadline) return Unavailable(requests, "batch did not end before the poll timeout");
            await _delay(_opts.PollInterval, ct).ConfigureAwait(false);
        }

        string resultsJsonl;
        try
        {
            resultsJsonl = await transport
                .GetJsonAsync($"{AnthropicWire.BatchesPath}/{batchId}/results", ct).ConfigureAwait(false);
        }
        catch (ModelTransportException ex)
        {
            return Unavailable(requests, $"batch results failed: {ex.Message}");
        }

        // Results arrive in ANY order, so they are indexed by custom_id and the response is rebuilt in
        // REQUEST order. Returning them in arrival order would silently mis-attribute every answer the
        // moment the vendor's ordering changed — the single most likely quiet bug in a batch client.
        var byId = AnthropicWire.ParseBatchResults(resultsJsonl)
            .GroupBy(p => p.CustomId)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var outp = new List<AnalysisResult>(requests.Count);
        foreach (var r in requests)
        {
            outp.Add(byId.TryGetValue(r.CustomId, out var parsed)
                ? ToResult(parsed, r.Task, batched: true)
                // A request the batch never answered for is a no-read for that id, not an exception:
                // the other requests in the same batch are still good.
                : new AnalysisResult(r.CustomId, AnalysisOutcome.Unavailable, "", TokenUsage.Zero, "", "no result for custom_id"));
        }
        return outp;
    }

    public async Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = llm.ModelFor(request.Task.Wire());
        var body = AnthropicWire.BuildMessageBody(
            model, request.Prompt, _opts.MaxOutputTokens, llm.PromptCacheStaticBlock);

        try
        {
            var json = await transport.PostJsonAsync(AnthropicWire.MessagesPath, body, ct).ConfigureAwait(false);
            var parsed = AnthropicWire.ParseMessage(request.CustomId, JsonNode.Parse(json));
            return ToResult(parsed, request.Task, batched: false);
        }
        catch (ModelTransportException ex)
        {
            return new AnalysisResult(
                request.CustomId, AnalysisOutcome.Unavailable, "", TokenUsage.Zero, model, ex.Message);
        }
    }

    private AnalysisResult ToResult(AnthropicWire.ParsedResult parsed, AnalysisTask task, bool batched)
    {
        // Cost the model that actually SERVED the call when it reported one, falling back to the pinned
        // model otherwise — D104 artefact (d) is about what ran, not what was asked for.
        var model = parsed.Model is { Length: > 0 } ? parsed.Model : llm.ModelFor(task.Wire());
        var usage = CostModel.Usage(
            parsed.Usage.Input, parsed.Usage.Output, parsed.Usage.CacheRead, parsed.Usage.CacheWrite,
            llm.PricingFor(model), llm.BatchDiscountMultiplier, batched);

        return new AnalysisResult(parsed.CustomId, parsed.Outcome, parsed.RawOutput, usage, model, parsed.Detail);
    }

    private static List<AnalysisResult> Unavailable(IReadOnlyList<AnalysisRequest> requests, string detail)
        => [.. requests.Select(r =>
            new AnalysisResult(r.CustomId, AnalysisOutcome.Unavailable, "", TokenUsage.Zero, "", detail))];
}
