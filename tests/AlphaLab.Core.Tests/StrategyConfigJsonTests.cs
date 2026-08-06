using System.Text.Json;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Json;

namespace AlphaLab.Core.Tests;

/// <summary>
/// D133 — `strategies.config_json` becomes readable, and the four properties that bind together:
/// TYPED, ADDITIVE, TOLERANT, BYTE-STABLE.
///
/// The load-bearing test is <see cref="D133_ConfigJson_RoundTripsEveryFrozenRow"/>: D17 forbids
/// re-serializing over a frozen row, so a reader that could not reproduce an existing row's bytes would
/// make the column unreadable exactly where it is guaranteed unrewritable.
/// </summary>
public class StrategyConfigJsonTests
{
    /// <summary>The exact shape `DummyRoster` freezes today — the three rows in every live store.</summary>
    private static StrategyConfig BuyAndHoldConfig() => new()
    {
        Seed = 1,
        Selection = SelectionRule.TopN(1),
        Sizing = SizingMode.Equal,
    };

    private static StrategyConfig ThresholdConfig() => new()
    {
        Seed = 7,
        Selection = SelectionRule.Threshold(0.6, 15),
        Sizing = SizingMode.Equal,
        Params = new Dictionary<string, double> { ["lookback"] = 50 },
        Unregistered = true,
    };

    [Fact]
    public void D133_ConfigJson_RoundTripsEveryFrozenRow()
    {
        // Written the way DummyRoster writes today: the typed record through AlphaLabJson.Options.
        foreach (var original in new[] { BuyAndHoldConfig(), ThresholdConfig() })
        {
            var asFrozenToday = JsonSerializer.Serialize(original, AlphaLabJson.Options);

            var read = StrategyConfigJson.Read(asFrozenToday);

            Assert.NotNull(read);
            Assert.Equal(original.Seed, read!.Seed);
            Assert.Equal(original.Sizing, read.Sizing);
            Assert.Equal(original.Selection, read.Selection);
            Assert.Equal(original.Unregistered, read.Unregistered);
            Assert.Equal(original.Params, read.Params);

            // BYTE-IDENTICAL on re-write: the whole point. If this ever fails, a frozen row cannot be
            // reproduced from what was read back, and the fork rule has no witness.
            Assert.Equal(StrategyConfigJson.Write(original), StrategyConfigJson.Write(read));
        }
    }

    [Fact]
    public void D133_Read_IsTolerantOfEveryHistoricalPayload()
    {
        // `{}` — what EvaluationStepTests, CohortMaturationBuilderTests and RecomputeParityTests all seed.
        Assert.NotNull(StrategyConfigJson.Read("{}"));

        // ReplayRunner's anonymous plant object: unknown keys, none of the typed members.
        var plant = """{"plant":"edge","kind":"edge","family":"plant","alpha_ann_pct":2.0,"seed":11,"unregistered":true}""";
        var read = StrategyConfigJson.Read(plant);
        Assert.NotNull(read);
        Assert.True(read!.Unregistered);   // the one member it does carry is honoured

        // Payloads that carry no config at all resolve to null rather than throwing — the caller
        // reports it; the daily run does not die on a legacy fixture.
        Assert.Null(StrategyConfigJson.Read(null));
        Assert.Null(StrategyConfigJson.Read(""));
        Assert.Null(StrategyConfigJson.Read("not json"));
        Assert.Null(StrategyConfigJson.Read("[1,2,3]"));
    }

    [Fact]
    public void D133_Write_IsByteStable_RegardlessOfDictionaryOrder()
    {
        var a = new StrategyConfig
        {
            Seed = 3,
            Selection = SelectionRule.TopN(40),
            Sizing = SizingMode.Equal,
            Params = new Dictionary<string, double> { ["lookback"] = 126, ["skip"] = 21, ["exit_rank"] = 80 },
            Frozen = new Dictionary<string, string> { ["model_id"] = "claude-opus-5", ["recipe"] = "cp-1.1" },
        };
        var b = a with
        {
            // Same content, different insertion order — enumeration order is not a contract.
            Params = new Dictionary<string, double> { ["exit_rank"] = 80, ["skip"] = 21, ["lookback"] = 126 },
            Frozen = new Dictionary<string, string> { ["recipe"] = "cp-1.1", ["model_id"] = "claude-opus-5" },
        };

        Assert.Equal(StrategyConfigJson.Write(a), StrategyConfigJson.Write(b));
    }

    [Fact]
    public void D133_FrozenStringsAndSets_RoundTrip_AndFailClosedWhenAbsent()
    {
        var config = new StrategyConfig
        {
            Seed = 5,
            Selection = SelectionRule.TopN(25),
            Sizing = SizingMode.Equal,
            Frozen = new Dictionary<string, string> { ["shortlist_recipe"] = "sr-1.0" },
            // D127's dispersion signal set: ORDER is part of the frozen value and is never sorted.
            FrozenSets = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dispersion_signals"] = ["mom:L252s21", "bab:L252", "lowvol:L252"],
            },
        };

        var read = StrategyConfigJson.Read(StrategyConfigJson.Write(config));

        Assert.NotNull(read);
        Assert.Equal("sr-1.0", read!.FrozenValue("shortlist_recipe"));
        Assert.Equal(["mom:L252s21", "bab:L252", "lowvol:L252"], read.FrozenSet("dispersion_signals"));

        // Fails closed, like Param: a missing frozen param is a config error, never a default.
        Assert.Throws<KeyNotFoundException>(() => read.FrozenValue("model_id"));
        Assert.Throws<KeyNotFoundException>(() => read.FrozenSet("nope"));
    }

    /// <summary>
    /// The hole D133 names: `holding_horizon_days` stores only the day count, so `ToRankExit` and
    /// `ToNextRebalance` both persist as NULL and were indistinguishable when read back.
    /// </summary>
    [Fact]
    public void D133_HorizonShape_SurvivesTheRoundTrip_WhereTheDayColumnCannotDistinguishIt()
    {
        HoldingHorizon[] shapes = [new HoldingHorizon.ToRankExit(), new HoldingHorizon.ToNextRebalance()];

        foreach (var shape in shapes)
        {
            Assert.Null(shape.Days_);   // the column would store NULL for BOTH — the defect

            var read = StrategyConfigJson.Read(StrategyConfigJson.Write(new StrategyConfig
            {
                Seed = 1, Selection = SelectionRule.TopN(1), Sizing = SizingMode.Equal, Horizon = shape,
            }));

            Assert.NotNull(read);
            Assert.Equal(shape, read!.Horizon);       // recovered, discriminator and all
            Assert.Equal(shape.GetType(), read.Horizon!.GetType());
        }
    }

    [Fact]
    public void D133_UnregisteredMarker_IsStampedThroughTheShape_NotAroundIt()
    {
        // A readable config keeps every other member and gains the flag.
        var stamped = StrategyConfigJson.WithUnregisteredMarker(StrategyConfigJson.Write(ThresholdConfig() with { Unregistered = false }));
        var read = StrategyConfigJson.Read(stamped);
        Assert.NotNull(read);
        Assert.True(read!.Unregistered);
        Assert.Equal(50, read.Param("lookback"));     // the rest survived the stamp

        // A payload that is NOT a readable config still gets honestly marked.
        var plant = StrategyConfigJson.WithUnregisteredMarker("""{"plant":"edge"}""");
        Assert.Contains("\"unregistered\":true", plant.Replace(" ", ""), StringComparison.Ordinal);
    }
}
