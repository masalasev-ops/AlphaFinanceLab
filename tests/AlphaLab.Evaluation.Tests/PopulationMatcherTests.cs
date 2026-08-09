using AlphaLab.Core.Domain;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Monitor;
using AlphaLab.Evaluation.Populations;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// PopulationHookup_MatchesByCadenceFamily — checkpoint 6.3's gate. STRATEGY_CATALOG §5.2/§13 has
/// asserted since v6 that "the factory wires every new candidate to its matched random population by
/// cadence family"; until now the pipeline hardcoded the daily population for EVERY promotable strategy
/// and said so in a comment, and `CandidateFactory` contained no population code at all.
/// </summary>
public class PopulationMatcherTests
{
    private static IReadOnlyList<string> Dates(int n) =>
        Enumerable.Range(0, n).Select(i => new DateOnly(2024, 1, 1).AddDays(i).ToString("yyyy-MM-dd")).ToList();

    private static void SeedRow(EvalArena arena, string strategyId, string? declaredFamily)
    {
        using var db = arena.Open();
        var config = new StrategyConfig
        {
            Seed = 1, Selection = SelectionRule.TopN(10), Sizing = SizingMode.Equal,
            Frozen = declaredFamily is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [CadenceFamily.FrozenKey] = declaredFamily },
        };
        db.Strategies.Add(new StrategyRow
        {
            StrategyId = strategyId, Family = "test", ConfigJson = StrategyConfigJson.Write(config),
            ExitPolicyJson = "{}", CreatedOn = "2024-01-01", Status = "candidate",
        });
        db.SaveChanges();
    }

    [Fact]
    public void PopulationHookup_MatchesByCadenceFamily()
    {
        using var arena = new EvalArena();
        var dates = Dates(5);
        var daily = arena.SeedPopulation(CadenceFamily.Daily, costsOn: true, seed: 1001, dates, _ => [0.001, 0.001, 0.001, 0.001], m: 2);
        var monthly = arena.SeedPopulation(CadenceFamily.Monthly, costsOn: true, seed: 1003, dates, _ => [0.002, 0.002, 0.002, 0.002], m: 2);

        SeedRow(arena, "s:daily", CadenceFamily.Daily);
        SeedRow(arena, "s:monthly", CadenceFamily.Monthly);

        using var db = arena.Open();
        var matcher = PopulationMatcher.ByDeclaration(db);

        Assert.Equal(new PopulationMatch(CadenceFamily.Daily, daily), matcher.For("s:daily"));
        Assert.Equal(new PopulationMatch(CadenceFamily.Monthly, monthly), matcher.For("s:monthly"));
        Assert.NotEqual(daily, monthly);   // the two really are different nulls, not one row read twice
    }

    /// <summary>
    /// Hard rule 9 / OVERFITTING_MONITOR §2: the cost-free population is DISPLAY-ONLY and never an S3
    /// comparator. It shares BOTH the family token "daily" and the daily seed with its cost-on twin, so
    /// a resolver keyed on the name alone would return either — the predicate is the boundary, not
    /// decoration. Seeded cost-free FIRST so a name-only match would pick the wrong row.
    /// </summary>
    [Fact]
    public void PopulationHookup_NeverResolvesToTheCostFreeTwin_EvenThoughItSharesTheFamilyToken()
    {
        using var arena = new EvalArena();
        var dates = Dates(4);
        var costFree = arena.SeedPopulation(CadenceFamily.Daily, costsOn: false, seed: 1001, dates, _ => [0.0, 0.0, 0.0], m: 1);
        var costOn = arena.SeedPopulation(CadenceFamily.Daily, costsOn: true, seed: 1001, dates, _ => [0.001, 0.001, 0.001], m: 1);
        SeedRow(arena, "s:daily", CadenceFamily.Daily);

        using var db = arena.Open();
        var match = PopulationMatcher.ByDeclaration(db).For("s:daily");

        Assert.Equal(costOn, match.PopulationId);
        Assert.NotEqual(costFree, match.PopulationId);
    }

    /// <summary>
    /// The compatibility rule, with its reason: every row frozen before the key existed — the three
    /// Phase-2 dummies and the D64 plants — carries no declaration, and D17 forbids re-serializing over
    /// a frozen row to add one. Resolving them to daily is what keeps the frozen generation judged
    /// exactly as it was judged.
    /// </summary>
    [Fact]
    public void PopulationHookup_ARowFrozenBeforeTheKeyExisted_ResolvesToTheCompatibilityFamily()
    {
        using var arena = new EvalArena();
        var dates = Dates(4);
        var daily = arena.SeedPopulation(CadenceFamily.Daily, costsOn: true, seed: 1001, dates, _ => [0.001, 0.001, 0.001], m: 1);

        SeedRow(arena, "s:undeclared", declaredFamily: null);
        using (var db = arena.Open())
        {
            // The literal pre-D133 shape too: a plant row's config_json is not a readable config at all.
            db.Strategies.Add(new StrategyRow
            {
                StrategyId = "plant:x", Family = "plant", ConfigJson = "{}", ExitPolicyJson = "{}",
                CreatedOn = "2024-01-01", Status = "candidate",
            });
            db.SaveChanges();
        }

        using var read = arena.Open();
        var matcher = PopulationMatcher.ByDeclaration(read);
        Assert.Equal(new PopulationMatch(CadenceFamily.Daily, daily), matcher.For("s:undeclared"));
        Assert.Equal(new PopulationMatch(CadenceFamily.Daily, daily), matcher.For("plant:x"));
    }

    /// <summary>
    /// FAIL CLOSED, in catalog §5.2's own words: "until it exists, S3 is undefined for those strategies".
    /// A declared family with no spawned population must NOT fall back to the daily null — judging a
    /// monthly strategy against a daily null is precisely the mismatch this checkpoint removes, so doing
    /// it on the error path would reintroduce it exactly where nobody would look.
    /// </summary>
    [Fact]
    public void PopulationHookup_ADeclaredFamilyWithNoSpawnedPopulation_ResolvesToNothing_NeverToDaily()
    {
        using var arena = new EvalArena();
        var dates = Dates(4);
        var daily = arena.SeedPopulation(CadenceFamily.Daily, costsOn: true, seed: 1001, dates, _ => [0.001, 0.001, 0.001], m: 1);

        SeedRow(arena, "s:quarterly", CadenceFamily.Quarterly);   // reserved seed, never spawned
        SeedRow(arena, "s:event", "event");                       // arrives with Breakout at 6.13

        using var db = arena.Open();
        var matcher = PopulationMatcher.ByDeclaration(db);

        var quarterly = matcher.For("s:quarterly");
        Assert.Equal(CadenceFamily.Quarterly, quarterly.Family);
        Assert.Null(quarterly.PopulationId);
        Assert.NotEqual(daily, quarterly.PopulationId);

        // An UNKNOWN token is used as declared rather than silently replaced by the default: no
        // population, S3 undefined — louder and more accurate than substituting a null it never asked for.
        var ev = matcher.For("s:event");
        Assert.Equal("event", ev.Family);
        Assert.Null(ev.PopulationId);
    }

    /// <summary>
    /// The consequence at the point that matters: an unresolvable null makes S3 UNDEFINED rather than
    /// judging the strategy against someone else's distribution.
    /// </summary>
    [Fact]
    public void PopulationHookup_AnUnresolvedFamily_LeavesS3Undefined_RatherThanJudgingAgainstTheWrongNull()
    {
        using var arena = new EvalArena();
        var dates = Dates(30);
        arena.SeedPopulation(CadenceFamily.Daily, costsOn: true, seed: 1001, dates,
            _ => Enumerable.Repeat(0.0005, dates.Count - 1).ToList(), m: 5);
        arena.SeedStrategy("buyhold:cw", "baseline", dates, Enumerable.Repeat(0.0004, dates.Count - 1).ToList());
        arena.SeedStrategy("s:quarterly", "candidate", dates, Enumerable.Repeat(0.0009, dates.Count - 1).ToList());
        SeedRow(arena, "s:quarterly-cfg", CadenceFamily.Quarterly);

        using (var db = arena.Open())
        {
            // Point the seeded strategy row at the quarterly declaration.
            var row = db.Strategies.Single(s => s.StrategyId == "s:quarterly");
            row.ConfigJson = db.Strategies.Single(s => s.StrategyId == "s:quarterly-cfg").ConfigJson;
            db.Strategies.Remove(db.Strategies.Single(s => s.StrategyId == "s:quarterly-cfg"));
            db.SaveChanges();
        }

        using var read = arena.Open();
        var result = new OverfittingMonitor(read, new AlphaLab.Core.Config.GateOptions())
            .Run(dates[^1], "buyhold:cw", PopulationMatcher.ByDeclaration(read), "live", EvalArena.Watermark(dates[^1]))
            .Single(r => r.StrategyId == "s:quarterly");

        Assert.Equal("undefined", result.S3.Contribution);
        Assert.Null(result.S3.Value);
    }
}
