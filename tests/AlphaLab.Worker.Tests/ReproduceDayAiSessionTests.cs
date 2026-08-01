using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Worker.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// `FX-ReproduceDay-AiSession` (D105): `reproduce-day` on a session containing an AI decision replays the
/// persisted <c>ai_decisions</c> row and makes **zero** model calls.
///
/// **Why this fixture is load-bearing rather than belt-and-braces.** `FX-ReproduceDay` asserts
/// byte-identical output. For an AI-seated session that is satisfiable ONLY by replaying the stored
/// decision — a live call returns something different and fails the assertion on a **correctly-committed**
/// day, turning the lab's strongest reproducibility claim into a test that cannot pass (§13.5).
/// </summary>
public class ReproduceDayAiSessionTests
{
    private static string TempConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), $"alphalab-repro-{Guid.NewGuid():N}.db")}";

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void FX_ReproduceDay_AiSession_TheScratchGraphHasNoModelProvider()
    {
        // The zero-API-call guarantee is STRUCTURAL: reproduce-day builds its scratch services from
        // AddDailyPipelineCore and never calls AddForwardLlmStage, so there is no provider to call.
        // Asserted on the composition rather than by counting calls at runtime, for the same reason
        // FR21_Replay_HasNoAnalysisPath is: a call counter proves what one run did, an absent
        // registration proves what no run can do.
        var services = new ServiceCollection();
        services.AddDailyPipelineCore(
            Config(("Arena:Id", "test")),
            new ArenaOptions { Id = "test" },
            TempConnectionString(),
            ensureDirectory: false);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAnalysisProvider));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IModelTransport));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPostCommitStage));
    }

    [Fact]
    public void FX_ReproduceDay_AiSession_TheDecisionRowSurvivesTheRewind()
    {
        // The other half, and the one that is easy to get backwards. ai_decisions must be UNTOUCHED:
        // rewinding it would delete the very row the replay depends on and force the choice between
        // calling the model (forbidden by D105) and having no answer at all.
        //
        // Read off ScratchStore's own classification, which ScratchStoreClassificationTests separately
        // proves is exhaustive — so this asserts the CHOICE, not the coverage.
        var untouched = ScratchStoreClassification.UntouchedTables();

        Assert.Contains("ai_decisions", untouched);
        Assert.Contains("ai_context_packs", untouched);

        // And the contrast that makes the choice meaningful: a GRADE is an input a reproduced session
        // would re-produce, so it is rewound; a DECISION is a record the reproduction must consume.
        Assert.DoesNotContain("signal_ic", untouched);
    }
}

/// <summary>Reflection helper: ScratchStore's classification lists are private, and a test that copied
/// them would assert against its own copy rather than against the code.</summary>
internal static class ScratchStoreClassification
{
    public static IReadOnlyList<string> UntouchedTables() => Read("Untouched");

    public static IReadOnlyList<string> RewoundTables() => Read("RewoundTables");

    private static IReadOnlyList<string> Read(string field)
    {
        var f = typeof(Worker.Ops.ScratchStore).GetField(
            field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException($"ScratchStore.{field} not found — the classification moved.");
        return (string[])f.GetValue(null)!;
    }
}
