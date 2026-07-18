using System.Text.Json;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Json;

namespace AlphaLab.Core.Tests;

/// <summary>
/// FR-8 / catalog §2 — ExitPolicy is declarative and serialized into strategies.exit_policy_json,
/// so its JSON round-trip IS the persistence contract. All five declared shapes must survive it,
/// including the two Phase-2 refuses to execute: a strategy row written in Phase 6 must be
/// readable, and an exit_policy_json that cannot round-trip is a corrupt strategy.
/// </summary>
public class ExitPolicyTests
{
    private static string Serialize(ExitPolicy p) => JsonSerializer.Serialize(p, AlphaLabJson.Options);

    private static ExitPolicy Deserialize(string json) =>
        JsonSerializer.Deserialize<ExitPolicy>(json, AlphaLabJson.Options)!;

    public static TheoryData<ExitPolicy, string> AllShapes => new()
    {
        { new ExitPolicy.Never(), "never" },
        { new ExitPolicy.RankBuffer(80), "rank_buffer" },
        { new ExitPolicy.ScheduledRebalance(21), "scheduled_rebalance" },
        { new ExitPolicy.TargetOrTimeStop("rsi>50", 10), "target_or_time_stop" },
        { new ExitPolicy.ChannelExit(20), "channel_exit" },
    };

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void FR8_ExitPolicy_RoundTripsWithItsKindDiscriminator(ExitPolicy policy, string kind)
    {
        var json = Serialize(policy);

        Assert.Contains($"\"kind\":\"{kind}\"", json);
        Assert.Equal(policy, Deserialize(json)); // records compare structurally
    }

    [Fact]
    public void FR8_ExitPolicy_UnknownKind_Throws_NeverDefaults()
    {
        // Hard rule 10: an unrecognized exit policy must fail closed. Silently defaulting to
        // Never would turn an unknown rule into buy-and-hold — a position that never exits.
        var json = """{"kind":"teleport","exitRank":80}""";

        Assert.ThrowsAny<JsonException>(() => Deserialize(json));
    }

    [Fact]
    public void FR8_ExitPolicy_CarriesItsParameters_ThroughPersistence()
    {
        // The parameters are the policy — a round-trip that keeps the kind but drops exitRank
        // would silently re-band a live momentum strategy.
        var restored = Assert.IsType<ExitPolicy.RankBuffer>(Deserialize(Serialize(new ExitPolicy.RankBuffer(80))));
        Assert.Equal(80, restored.ExitRank);

        var mixed = Assert.IsType<ExitPolicy.TargetOrTimeStop>(
            Deserialize(Serialize(new ExitPolicy.TargetOrTimeStop("rsi>50", 10))));
        Assert.Equal("rsi>50", mixed.ExitCondition);
        Assert.Equal(10, mixed.MaxHoldDays);
    }

    [Fact]
    public void FR8_ExitPolicy_HierarchyIsClosed_SoStage4SwitchIsExhaustive()
    {
        // Stage 4's executor switches over these shapes. If a sixth could be declared elsewhere,
        // the switch would silently fall through to "no exit" for it. The private constructor is
        // what forecloses that; this asserts the guarantee rather than trusting the comment.
        var external = typeof(ExitPolicy).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ExitPolicy)) && t.DeclaringType != typeof(ExitPolicy));

        Assert.Empty(external);
    }
}
