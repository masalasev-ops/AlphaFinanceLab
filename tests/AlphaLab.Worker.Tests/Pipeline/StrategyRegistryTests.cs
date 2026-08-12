using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Data.Entities;
using AlphaLab.Strategies;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// The registry that turns a PERSISTED row into a runnable plan (6.2), replacing Phase 2's id switch.
///
/// The two per-account run-settings seams stated at checkpoint 2.9 still hold for the three legacy
/// dummies — cap-weight needs a 100% position cap; equal-weight needs a breadth ceiling above the index
/// — and the row-driven path is exercised beside them, because the whole point of 6.2 is that what runs
/// is derived from what was FROZEN rather than from a hardcoded id.
/// </summary>
public class StrategyRegistryTests
{
    private static StrategyRow Row(string id, string family, string configJson) => new()
    {
        StrategyId = id, Family = family, ConfigJson = configJson,
        ExitPolicyJson = "{}", CreatedOn = "2026-08-06", Status = "candidate",
    };

    /// <summary>
    /// THE VERBATIM `config_json` OF THE THREE ROWS IN THE LIVE sp500 ARENA, copied byte-for-byte from
    /// the store rather than rebuilt from the C# factories (D152, finding 422).
    ///
    /// <para>These fixtures used to pass <c>"{}"</c>, which meant they asserted the run settings while
    /// saying nothing at all about whether the executed model matched anything frozen — and `"{}"` reads
    /// back as the TYPE defaults, so under D152's check it now correctly refuses. Rebuilding the expected
    /// bytes from <c>BuyAndHoldModel.CapWeight().Config</c> would restore green while comparing code to
    /// code, which is exactly the tautology this finding is about. Literals from the store compare code
    /// to the STORE, so an edit to a `Create(...)` default reddens here.</para>
    /// </summary>
    private const string FrozenCapWeight =
        """{"seed":0,"selection":{"mode":"top_n","n":1,"min_score":0.6,"max_concurrent":60},"sizing":"equal","params":{},"unregistered":false}""";

    private const string FrozenEqualWeight =
        """{"seed":0,"selection":{"mode":"top_n","n":100000,"min_score":0.6,"max_concurrent":60},"sizing":"equal","params":{},"unregistered":false}""";

    private const string FrozenThreshold =
        """{"seed":0,"selection":{"mode":"threshold","n":40,"min_score":0.6,"max_concurrent":60},"sizing":"equal","params":{"lookback":50},"unregistered":true}""";

    [Fact]
    public void CapWeight_HoldsTheProxyAtA100PercentCap()
    {
        var cw = StrategyRegistry.ForRow(Row("buyhold:cw", "passive", FrozenCapWeight));
        Assert.NotNull(cw);
        Assert.Equal(UniverseScope.CapWeightProxy, cw!.Universe);
        Assert.Equal(1.0, cw.Sizing.PositionCapPct); // a single name can be a full position
    }

    [Fact]
    public void EqualWeight_TradesTheWholeIndexWithABreadthCeilingAboveIt()
    {
        var ew = StrategyRegistry.ForRow(Row("buyhold:ew", "passive", FrozenEqualWeight));
        Assert.NotNull(ew);
        Assert.Equal(UniverseScope.FullIndex, ew!.Universe);
        Assert.True(ew.Guardrails.MaxConcurrentPositions >= 500); // holds the whole roster
    }

    [Fact]
    public void Threshold_TradesTheFullIndex()
    {
        var th = StrategyRegistry.ForRow(Row("threshold:sma50", "passive", FrozenThreshold));
        Assert.NotNull(th);
        Assert.Equal(UniverseScope.FullIndex, th!.Universe);

        // The name used to end "_AtDefaults", which was the defect in one word: the plan was accepted
        // BECAUSE it came from the C# defaults, with the frozen row never consulted.
        Assert.Equal(SelectionMode.Threshold, th.Model.Config.Selection.Mode);
        Assert.Equal(50, th.Model.Config.Params["lookback"]);
    }

    [Fact]
    public void D152_ALegacyDummyWhoseCodeDivergedFromItsFrozenRow_IsRefused_NotSilentlyRun()
    {
        // Hard rule 8: a change to a live strategy forks a new strategy_id. Nothing enforced it, because
        // DummyRoster.RegisterStrategy is idempotent — an edit to a Create(...) default would be written
        // NOWHERE and executed EVERYWHERE, with no fork, no trials_registry row and no log line. The
        // divergence is simulated from the row side (a frozen lookback of 26 against the code's 50),
        // which is the same disagreement an edit to the code would produce from the other direction.
        var tampered = FrozenThreshold.Replace("\"lookback\":50", "\"lookback\":26", StringComparison.Ordinal);
        Assert.NotEqual(FrozenThreshold, tampered);   // the fixture must actually differ, or it proves nothing

        var ex = Assert.Throws<InvalidOperationException>(
            () => StrategyRegistry.ForRow(Row("threshold:sma50", "passive", tampered)));

        Assert.Contains("threshold:sma50", ex.Message, StringComparison.Ordinal);
        Assert.Contains("lookback", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rule 8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void D152_EveryFieldTheFunnelConsumes_IsCovered_NotJustTheOneTheFixtureHappensToMove()
    {
        // A single-field check would pass the test above while leaving the other five unguarded. Each
        // mutation below is a field the funnel actually reads off StrategyConfig.
        var mutations = new (string Field, string Json)[]
        {
            ("seed",                    FrozenThreshold.Replace("\"seed\":0", "\"seed\":7", StringComparison.Ordinal)),
            ("selection.mode",          FrozenThreshold.Replace("\"mode\":\"threshold\"", "\"mode\":\"top_n\"", StringComparison.Ordinal)),
            ("selection.n",             FrozenThreshold.Replace("\"n\":40", "\"n\":41", StringComparison.Ordinal)),
            ("selection.min_score",     FrozenThreshold.Replace("\"min_score\":0.6", "\"min_score\":0.7", StringComparison.Ordinal)),
            ("selection.max_concurrent",FrozenThreshold.Replace("\"max_concurrent\":60", "\"max_concurrent\":61", StringComparison.Ordinal)),
            ("params",                  FrozenThreshold.Replace("\"lookback\":50", "\"lookback\":51", StringComparison.Ordinal)),
        };

        foreach (var (field, json) in mutations)
        {
            Assert.NotEqual(FrozenThreshold, json);   // anti-vacuity: the mutation must have applied
            var ex = Assert.Throws<InvalidOperationException>(
                () => StrategyRegistry.ForRow(Row("threshold:sma50", "passive", json)));
            Assert.Contains("rule 8", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void D152_AnUnreadableFrozenRow_IsUnknown_NotAThrow()
    {
        // The two failure modes are deliberately different sentences. An unreadable row means this build
        // cannot tell what was frozen — rule 10's "unknown", which the caller skips with a reason. A
        // READABLE row that disagrees is a rule-8 breach, which throws. Collapsing them would either
        // abort a day over an unparseable legacy payload or let a real divergence pass as "unknown".
        Assert.Null(StrategyRegistry.ForRow(Row("threshold:sma50", "passive", "not json at all")));
    }

    /// <summary>
    /// Rule 10, and the behaviour `RunDay_UnknownStrategyAccount_Skipped_RunStillCommits` pins: a family
    /// this build cannot construct resolves to null so the caller skips it with a reason. It never
    /// guesses a model, and it never falls back to some other family's.
    /// </summary>
    [Fact]
    public void UnknownFamily_ResolvesToNull_NotAGuess()
    {
        Assert.Null(StrategyRegistry.ForRow(Row("momentum:L126:K21:N40", "momentum", "{}")));
    }

    /// <summary>
    /// The legacy dummies are resolved by id BECAUSE their rows were frozen before D133 made config_json
    /// readable — they carry none of the run settings a row-driven family reads, and D17 forbids
    /// re-serializing over a frozen row to add them. This pins that the fallback is a CLOSED set: an
    /// unknown id with an unknown family gets nothing, even with a perfectly readable config.
    /// </summary>
    [Fact]
    public void TheLegacyFallbackIsAClosedSet_AReadableConfigDoesNotOpenIt()
    {
        var readable = StrategyConfigJson.Write(new StrategyConfig
        {
            Seed = 1, Selection = SelectionRule.TopN(40), Sizing = SizingMode.Equal,
        });

        Assert.Null(StrategyRegistry.ForRow(Row("buyhold:something_else", "passive", readable)));
    }
}
