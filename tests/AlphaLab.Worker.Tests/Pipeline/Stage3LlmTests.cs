using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Worker.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// Stage 3 of the D53 pipeline (FR-29): the LLM batch runs post-commit in its own transaction, never for
/// past days, and never in replay.
/// </summary>
public class Stage3LlmTests
{
    /// <summary>An absolute temp path: the store-path resolver refuses a relative one, because Worker,
    /// Api and the design-time factory run from different working directories and a relative path would
    /// open a DIFFERENT database per process.</summary>
    private static string TempConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), $"alphalab-stage3-{Guid.NewGuid():N}.db")}";

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void FR21_Replay_HasNoAnalysisPath()
    {
        // The core composition — which ReplayRunner and ReproduceDay both use — must register NO model
        // provider and NO post-commit stage. Asserted on the SERVICE COLLECTION rather than on runtime
        // behaviour: a runtime guard can be bypassed by a future caller, a missing registration cannot.
        var services = new ServiceCollection();
        services.AddDailyPipelineCore(
            Config(("Arena:Id", "test")),
            new ArenaOptions { Id = "test" },
            TempConnectionString(),
            ensureDirectory: false);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAnalysisProvider));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPostCommitStage));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IModelTransport));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(INewsProvider));
    }

    [Fact]
    public void ForwardComposition_IsTheOnlyOneThatRegistersAModelProvider()
    {
        // The positive half. Without it, FR21_Replay_HasNoAnalysisPath would pass on a build where the
        // LLM was never wired anywhere at all — a green test proving nothing.
        var services = new ServiceCollection();
        services.AddDailyPipelineCore(
            Config(("Arena:Id", "test")),
            new ArenaOptions { Id = "test" },
            TempConnectionString(),
            ensureDirectory: false);
        services.AddForwardLlmStage(Config(
            ("Llm:Tasks:regime_brief:Model", "claude-opus-5"),
            ("Llm:Pricing:claude-opus-5:InputPerMTok", "5.0"),
            ("Llm:Pricing:claude-opus-5:OutputPerMTok", "25.0")));

        Assert.Contains(services, d => d.ServiceType == typeof(IAnalysisProvider));
        Assert.Contains(services, d => d.ServiceType == typeof(IPostCommitStage));
    }

    [Fact]
    public void ForwardComposition_BindsTheModelTier_FromConfig()
    {
        var services = new ServiceCollection();
        services.AddForwardLlmStage(Config(
            ("Llm:Tasks:regime_brief:Model", "claude-opus-5"),
            ("Llm:Tasks:news_extraction:Model", "claude-haiku-4-5"),
            ("Llm:Pricing:claude-opus-5:InputPerMTok", "5.0"),
            ("Llm:Pricing:claude-opus-5:OutputPerMTok", "25.0")));

        var llm = services.BuildServiceProvider().GetRequiredService<LlmOptions>();

        Assert.Equal("claude-opus-5", llm.ModelFor(AnalysisTaskNames.RegimeBrief));
        Assert.Equal("claude-haiku-4-5", llm.ModelFor(AnalysisTaskNames.NewsExtraction));
        Assert.Equal(5m, llm.PricingFor("claude-opus-5").InputPerMTok);
    }

    [Fact]
    public void ForwardComposition_UnconfiguredTask_FailsClosed()
    {
        // Rule 10: a missing tier THROWS rather than falling back to another task's model. A silent
        // substitution would be invisible downstream and would make D104 artefact (d) — the model string
        // that explains a behaviour change — a record of the wrong thing.
        var services = new ServiceCollection();
        services.AddForwardLlmStage(Config(("Llm:Tasks:regime_brief:Model", "claude-opus-5")));

        var llm = services.BuildServiceProvider().GetRequiredService<LlmOptions>();

        Assert.Throws<InvalidOperationException>(() => llm.ModelFor(AnalysisTaskNames.Skeptic));
    }

    [Fact]
    public void ForwardComposition_UnpricedModel_FailsClosed()
    {
        // A zero cost is indistinguishable from a free cache hit in llm_budget_log, so an unpriced model
        // would make the D24 ceiling unenforceable exactly when a newly-pinned model started spending.
        var services = new ServiceCollection();
        services.AddForwardLlmStage(Config(("Llm:Tasks:regime_brief:Model", "brand-new-model")));

        var llm = services.BuildServiceProvider().GetRequiredService<LlmOptions>();

        Assert.Throws<InvalidOperationException>(() => llm.PricingFor("brand-new-model"));
    }

    [Fact]
    public async Task RegimeBrief_EmptyAdmittedNews_IsANoReadDay_AndSpendsNothing()
    {
        // Calling the model with nothing to read would spend tokens to be told there was nothing to read.
        var analysis = new CountingAnalysisProvider();
        var stage = new RegimeBriefStage(
            analysis,
            new StubNews([]),
            null!,   // db is only reached for the regime label, which the empty-news path returns before
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RegimeBriefStage>.Instance);

        await stage.RunAsync(new PipelineDayContext("2026-08-03", new DateOnly(2026, 8, 3), "w", "live", null!));

        Assert.Equal(0, analysis.BatchCalls);
    }

    [Fact]
    public void StaticInstructions_ForbidTheRetiredSentimentScore()
    {
        // D46's framing is superseded by D79–D82 (golden rule 28). The brief is prose for a human — a
        // single machine-readable number is the thing that was retired, and the prompt says so rather
        // than relying on the model not to volunteer one.
        Assert.Contains("numeric sentiment score", RegimeBriefStage.StaticInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No trading recommendations", RegimeBriefStage.StaticInstructions, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubNews(IReadOnlyList<NewsArticle> articles) : INewsProvider
    {
        public Task<IReadOnlyList<NewsArticle>> GetAdmittedAsync(string asOf, CancellationToken ct = default)
            => Task.FromResult(articles);
    }

    private sealed class CountingAnalysisProvider : IAnalysisProvider
    {
        public int BatchCalls { get; private set; }

        public Task<IReadOnlyList<AnalysisResult>> RunBatchAsync(
            IReadOnlyList<AnalysisRequest> requests, CancellationToken ct = default)
        {
            BatchCalls++;
            return Task.FromResult<IReadOnlyList<AnalysisResult>>(
                [.. requests.Select(r => new AnalysisResult(
                    r.CustomId, AnalysisOutcome.Succeeded, "brief", TokenUsage.Zero, "m"))]);
        }

        public Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken ct = default)
            => Task.FromResult(new AnalysisResult(
                request.CustomId, AnalysisOutcome.Succeeded, "brief", TokenUsage.Zero, "m"));
    }
}
