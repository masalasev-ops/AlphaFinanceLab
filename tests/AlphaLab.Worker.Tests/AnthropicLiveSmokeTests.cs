using AlphaLab.Core.Llm;
using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;
using AlphaLab.Llm;
using Microsoft.Extensions.Configuration;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The ONE live smoke test against the real Anthropic endpoint (TEST_PLAN §6).
///
/// **Gated by trait, not by an env flag** — D67 forbids environment variables for configuration, and the
/// same reasoning applies to test gating: the exclusion belongs in the command line the build runs, where
/// it is visible in `ci.ps1`, not in a machine's environment where it is invisible and unreproducible.
/// `ci.ps1` and the GitHub workflow both pass `--filter "Category!=LiveSmoke"`.
///
/// Run it deliberately:
///   dotnet test tests/AlphaLab.Worker.Tests --filter "Category=LiveSmoke"
///
/// **A missing key FAILS with an actionable message rather than skipping.** The first draft skipped, on
/// the reasoning that a keyless developer should not get a red result — but that reasoning does not
/// survive the gating: this test never runs unless someone explicitly asks for it, and someone who asks
/// to run a live smoke test and has no key is better served by being told so than by a green tick over a
/// test that did nothing. (xUnit 2.9.3 also has no dynamic skip without an extra package, and adding one
/// to reach the weaker behaviour would have been the wrong trade.)
///
/// It closes the INTEGRATIONS §5 obligation that v1.9.60 could only half-close: that pass verified the
/// endpoint, headers, polling shape and result semantics **against the published reference**, and recorded
/// that the live confirmation was still owed. This is that confirmation, and it stays owed until someone
/// actually runs it — which is why the PROGRESS gate box asks for the result rather than the test's
/// existence.
/// </summary>
public class AnthropicLiveSmokeTests
{
    private static string? ApiKey()
    {
        // Exactly the D67 builder: appsettings.json + the gitignored secrets file. No env vars, no user
        // secrets. Walks up to the repo root because the test runs from its own output directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaLab.slnx"))) dir = dir.Parent;
        if (dir is null) return null;

        var secrets = Path.Combine(dir.FullName, "src", "AlphaLab.Worker", "appsettings.Secrets.json");
        if (!File.Exists(secrets)) return null;

        var cfg = new ConfigurationBuilder().AddJsonFile(secrets, optional: true).Build();
        var key = cfg["Secrets:AnthropicApiKey"];
        return string.IsNullOrWhiteSpace(key) || key.StartsWith("YOUR", StringComparison.OrdinalIgnoreCase)
            ? null
            : key;
    }

    /// <summary>
    /// The tiers the lab actually pins (v1.9.60), each exercised live.
    ///
    /// **`claude-opus-5` is the one that matters and was the one originally missed (finding 329).** All
    /// FOUR dispatched tasks — the Stage-3 regime brief, and the researcher seat's brief, skeptic and
    /// hypotheses — resolve to Opus 5. `news_extraction` is pinned to Haiku and **dispatched by nothing**:
    /// the D46 news budget is deterministic C#, so no code path calls that task at all. The first live run
    /// therefore verified the wire contract on the only tier the lab never calls.
    /// </summary>
    public static TheoryData<string, int> PinnedTiers => new()
    {
        // Opus 5 first, because it serves 100% of real traffic. max_tokens is sized with HEADROOM, not
        // for the answer: thinking is on by default on this tier and max_tokens caps thinking AND response
        // together, so a limit sized for "reply with one word" can be consumed entirely by thinking and
        // return empty content. That is the specific failure a Haiku call cannot surface.
        { "claude-opus-5", 2048 },
        { "claude-haiku-4-5", 64 },
    };

    [Theory]
    [MemberData(nameof(PinnedTiers))]
    [Trait("Category", "LiveSmoke")]
    public async Task LiveSmoke_BatchesEndpoint_AcceptsARequest_AndReturnsUsage(string model, int maxOutputTokens)
    {
        var key = ApiKey();
        Assert.True(
            key is not null,
            "Secrets:AnthropicApiKey is not configured in src/AlphaLab.Worker/appsettings.Secrets.json " +
            "(D67 — the gitignored secrets file is the only source; no env vars, no user secrets). " +
            "The live smoke test cannot run without it. It is excluded from every automated run, so this " +
            "failure means it was asked for deliberately.");

        using var http = new HttpClient();
        var transport = new AnthropicHttpTransport(
            new ResilientHttpClient(http),
            new AnthropicTransportOptions { ApiKey = key! });

        var llm = new Core.Config.LlmOptions
        {
            Tasks = { ["regime_brief"] = new Core.Config.LlmTaskOptions { Model = model } },
            Pricing =
            {
                [model] = new Core.Config.ModelPriceOptions { InputPerMTok = 5m, OutputPerMTok = 25m },
            },
        };

        var provider = new AnthropicAnalysisProvider(
            transport, llm, new AnthropicProviderOptions
            {
                MaxOutputTokens = maxOutputTokens,
                PollInterval = TimeSpan.FromSeconds(5),
                PollTimeout = TimeSpan.FromMinutes(10),
            });

        var request = new AnalysisRequest(
            "smoke-1",
            AnalysisTask.RegimeBrief,
            new PromptLayers("Reply with exactly the word OK.", "", "Reply now."));

        var results = await provider.RunBatchAsync([request]);

        var r = Assert.Single(results);
        Assert.Equal("smoke-1", r.CustomId);

        // A refusal or an outage is a legitimate live outcome and must not read as a wire-contract
        // failure — the assertion is that the round trip WORKED, whatever the model chose to say.
        Assert.True(
            r.Outcome is AnalysisOutcome.Succeeded or AnalysisOutcome.Refused,
            $"unexpected live outcome {r.Outcome} on {model}: {r.Detail}");

        if (r.Outcome is AnalysisOutcome.Succeeded)
        {
            // On Opus 5 an empty RawOutput is the SPECIFIC failure to watch for: thinking is on by
            // default and shares the max_tokens ceiling, so a too-small limit returns a well-formed
            // response with nothing in it. A green test over an empty answer would be worse than a red one.
            Assert.False(string.IsNullOrWhiteSpace(r.RawOutput),
                $"{model} returned an EMPTY answer. On a thinking-by-default tier this usually means " +
                $"max_tokens ({maxOutputTokens}) was consumed by thinking — size it with headroom, not " +
                "for the visible answer (INTEGRATIONS §5).");
            Assert.True(r.Usage.InputTokens > 0, "the API must report input tokens");
            Assert.True(r.Usage.CostUsd > 0m, "a real call must cost something");
            Assert.False(string.IsNullOrEmpty(r.ModelVersion), "the served model must be recorded (D104 (d))");

            // findings 328 + 329, checked on BOTH pinned tiers because ONE observation was not enough.
            //
            // The served model MAY be the bare alias or MAY be a dated snapshot, and it is per-family:
            // live, `claude-opus-5` comes back as `claude-opus-5` while `claude-haiku-4-5` comes back as
            // `claude-haiku-4-5-20251001`. The first live run saw only Haiku and the snapshot form was
            // generalised into a rule; running the tier that actually serves traffic refuted it.
            //
            // So the ONLY safe assertion is the prefix — which is exactly why `PricingFor` resolves
            // exact-first-then-longest-prefix: that rule is correct under BOTH forms, and any rule
            // assuming one of them is wrong half the time.
            Assert.StartsWith(model, r.ModelVersion, StringComparison.Ordinal);
        }
    }
}
