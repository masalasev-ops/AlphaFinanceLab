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

    [Fact]
    [Trait("Category", "LiveSmoke")]
    public async Task LiveSmoke_BatchesEndpoint_AcceptsARequest_AndReturnsUsage()
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
            Tasks = { ["regime_brief"] = new Core.Config.LlmTaskOptions { Model = "claude-haiku-4-5" } },
            Pricing =
            {
                ["claude-haiku-4-5"] = new Core.Config.ModelPriceOptions
                {
                    InputPerMTok = 1m,
                    OutputPerMTok = 5m,
                },
            },
        };

        // Deliberately the CHEAP tier and a tiny prompt: this proves the wire contract, not the model.
        var provider = new AnthropicAnalysisProvider(
            transport, llm, new AnthropicProviderOptions
            {
                MaxOutputTokens = 64,
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
            $"unexpected live outcome {r.Outcome}: {r.Detail}");

        if (r.Outcome is AnalysisOutcome.Succeeded)
        {
            Assert.NotEmpty(r.RawOutput);
            Assert.True(r.Usage.InputTokens > 0, "the API must report input tokens");
            Assert.True(r.Usage.CostUsd > 0m, "a real call must cost something");
            Assert.False(string.IsNullOrEmpty(r.ModelVersion), "the served model must be recorded (D104 (d))");

            // finding 328, pinned where it was found. The API resolves the pinned ALIAS to a dated
            // SNAPSHOT and reports the snapshot, so the served model is NOT the requested string — which
            // is invisible in every mocked test, because a fake transport echoes what it was asked for.
            // Costing the served model (D104 (d)) then met an exact-key price lookup and threw, killing
            // the whole forward path on real traffic. Asserted here rather than only in the unit test
            // because this is the only place the real string is observable.
            Assert.StartsWith("claude-haiku-4-5", r.ModelVersion, StringComparison.Ordinal);
            if (r.ModelVersion == "claude-haiku-4-5")
            {
                // Not a failure — a change in vendor behaviour worth noticing rather than absorbing.
                // If the API ever stops resolving aliases to snapshots, the prefix rule is still correct
                // but the finding's premise has changed.
                Assert.Fail(
                    "The API returned the bare ALIAS rather than a dated snapshot. finding 328's premise " +
                    "no longer holds; re-read INTEGRATIONS §5 before assuming the alias->snapshot rule.");
            }
        }
    }
}
