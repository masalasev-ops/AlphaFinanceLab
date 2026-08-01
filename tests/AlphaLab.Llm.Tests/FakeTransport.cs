using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm.Tests;

/// <summary>
/// A scripted <see cref="IModelTransport"/>. **This is the "mocked provider for CI" TEST_PLAN §6
/// requires** — every test below runs the full prompt-layering, batching, costing and refusal path with
/// no HTTP anywhere, which is the payoff the Core port was introduced for.
///
/// It also counts calls, so "spends nothing" can be asserted as *zero requests reached the transport*
/// rather than merely *the reported cost was zero* — a cache that called the API and then reported zero
/// would pass the weaker assertion.
/// </summary>
public sealed class FakeTransport : IModelTransport
{
    private readonly Queue<string> _postResponses = new();
    private readonly Queue<string> _getResponses = new();

    public List<string> PostedPaths { get; } = [];
    public List<string> PostedBodies { get; } = [];
    public List<string> GotPaths { get; } = [];

    /// <summary>Total requests that actually reached the transport.</summary>
    public int CallCount => PostedPaths.Count + GotPaths.Count;

    public FakeTransport EnqueuePost(string json) { _postResponses.Enqueue(json); return this; }
    public FakeTransport EnqueueGet(string json) { _getResponses.Enqueue(json); return this; }

    /// <summary>Make the next POST fail, to exercise the no-read-day path.</summary>
    public bool FailNextPost { get; set; }

    public Task<string> PostJsonAsync(string path, string jsonBody, CancellationToken ct = default)
    {
        PostedPaths.Add(path);
        PostedBodies.Add(jsonBody);
        if (FailNextPost)
        {
            FailNextPost = false;
            throw new ModelTransportException(503, "scripted transport failure");
        }
        return Task.FromResult(_postResponses.Count > 0 ? _postResponses.Dequeue() : "{}");
    }

    public Task<string> GetJsonAsync(string path, CancellationToken ct = default)
    {
        GotPaths.Add(path);
        return Task.FromResult(_getResponses.Count > 0 ? _getResponses.Dequeue() : "{}");
    }
}

/// <summary>In-memory <see cref="IAnalysisCache"/> — the store behaviour is exercised against real SQLite
/// in AlphaLab.Data.Tests; here the point is the provider's USE of the cache.</summary>
public sealed class FakeCache : IAnalysisCache
{
    private readonly Dictionary<string, CachedAnalysis> _rows = new(StringComparer.Ordinal);
    public int PutCount { get; private set; }

    private static string Key(string h, string m, string d) => $"{h}|{m}|{d}";

    public void Seed(string promptHash, string model, string asOf, string output) =>
        _rows[Key(promptHash, model, asOf)] = new CachedAnalysis(output, TokenUsage.Zero);

    public Task<CachedAnalysis?> TryGetAsync(string promptHash, string model, string asOf, CancellationToken ct = default)
        => Task.FromResult(_rows.TryGetValue(Key(promptHash, model, asOf), out var v) ? v : null);

    public Task PutAsync(
        string promptHash, string model, string asOf, AnalysisTask task,
        string rawOutput, TokenUsage usage, CancellationToken ct = default)
    {
        PutCount++;
        _rows[Key(promptHash, model, asOf)] = new CachedAnalysis(rawOutput, usage);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="ILlmBudgetLedger"/>.</summary>
public sealed class FakeLedger : ILlmBudgetLedger
{
    private readonly Dictionary<string, BudgetState> _days = new(StringComparer.Ordinal);
    public List<(string AsOf, int Calls, TokenUsage Usage, bool Degraded, string? Note)> Records { get; } = [];

    public void Seed(string asOf, BudgetState state) => _days[asOf] = state;

    public Task<BudgetState> GetAsync(string asOf, CancellationToken ct = default)
        => Task.FromResult(_days.TryGetValue(asOf, out var s) ? s : BudgetState.Empty);

    public Task RecordAsync(
        string asOf, int calls, TokenUsage usage, bool degraded, string? note, CancellationToken ct = default)
    {
        Records.Add((asOf, calls, usage, degraded, note));
        var prev = _days.TryGetValue(asOf, out var s) ? s : BudgetState.Empty;
        _days[asOf] = new BudgetState(
            prev.Calls + calls, prev.Tokens + usage.TotalTokens, prev.CostUsd + usage.CostUsd);
        return Task.CompletedTask;
    }
}

/// <summary>Shared option builders, so each test states only what it is actually about.</summary>
public static class TestOptions
{
    public const string OpusModel = "claude-opus-5";
    public const string HaikuModel = "claude-haiku-4-5";

    /// <summary>Options mirroring the v1.9.60 pinned tiers and the published rates recorded in
    /// CONFIG_REFERENCE.</summary>
    public static LlmOptions Llm(decimal maxCost = 1.00m, int maxCalls = 10, int maxTokens = 0) => new()
    {
        Tasks =
        {
            [AnalysisTaskNames.RegimeBrief] = new LlmTaskOptions { Model = OpusModel },
            [AnalysisTaskNames.ResearchBrief] = new LlmTaskOptions { Model = OpusModel },
            [AnalysisTaskNames.Skeptic] = new LlmTaskOptions { Model = OpusModel },
            [AnalysisTaskNames.Hypotheses] = new LlmTaskOptions { Model = OpusModel },
            [AnalysisTaskNames.NewsExtraction] = new LlmTaskOptions { Model = HaikuModel },
        },
        Pricing =
        {
            [OpusModel] = new ModelPriceOptions { InputPerMTok = 5m, OutputPerMTok = 25m },
            [HaikuModel] = new ModelPriceOptions { InputPerMTok = 1m, OutputPerMTok = 5m },
        },
        DailyBudget = new LlmDailyBudgetOptions
        {
            MaxCostUsd = maxCost,
            MaxCalls = maxCalls,
            MaxTokens = maxTokens,
        },
    };

    public static PromptLayers Prompt(string fresh = "today's rows") =>
        new("INSTRUCTIONS + SCHEMA", "lesson set", fresh);

    public static AnalysisRequest Request(string id, string fresh = "today's rows") =>
        new(id, AnalysisTask.RegimeBrief, Prompt(fresh));
}
