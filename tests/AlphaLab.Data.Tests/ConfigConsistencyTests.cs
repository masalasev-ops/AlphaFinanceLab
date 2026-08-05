using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Data;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Guards the "every process opens the SAME file" invariant (DB_RELOCATION.md): the Api, Worker,
/// AND Backfill-CLI appsettings.json <c>ConnectionStrings:AlphaLab</c> must each equal
/// <see cref="DbPathResolver.DefaultConnectionString"/> (which the EF design-time factory uses).
/// Together with that const these are the FOUR edit spots that must move together when the DB is
/// relocated. The CLI copy lives under <c>tools/</c> (not <c>src/</c>) and was added in checkpoint
/// 1.10 after this guard was first written — leaving it unchecked let a relocation half-apply and the
/// backfill silently write to the old path (finding 138, v1.9.10).
/// </summary>
public sealed class ConfigConsistencyTests
{
    [Theory]
    [InlineData("src", "AlphaLab.Api")]
    [InlineData("src", "AlphaLab.Worker")]
    [InlineData("tools", "Backfill")]
    public void Config_ConnectionString_EqualsResolverDefault(string subdir, string project)
    {
        var repoRoot = FindRepoRoot();
        var appsettings = Path.Combine(repoRoot, subdir, project, "appsettings.json");
        Assert.True(File.Exists(appsettings), $"missing {appsettings}");

        using var doc = JsonDocument.Parse(File.ReadAllText(appsettings));
        var cs = doc.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty("AlphaLab")
            .GetString();

        Assert.Equal(DbPathResolver.DefaultConnectionString, cs);
    }

    /// <summary>
    /// P1R-9 (finding 148): the connection-string guard above proves the four PATH-TEMPLATE copies are
    /// byte-identical — but the template is <c>Data Source=...\{Arena.Id}\alphalab.db</c>, a template with
    /// a blank in it. It cannot see WHAT fills that blank. Each process resolves <c>{Arena.Id}</c> from its
    /// OWN appsettings' <c>Arena:Id</c> (Api/Worker/Backfill), so three byte-identical connection strings
    /// can still open three DIFFERENT databases if their <c>Arena:Id</c> disagree — template equality does
    /// not imply resolved-path equality. This guard LOCKS the three backend <c>Arena:Id</c> values to a
    /// single agreed value (all "sp500" today).
    ///
    /// It is a lock over an already-correct state: it cannot "fail on current config" because the config is
    /// correct. The failing-on-current-code negative lives at the method level — see
    /// <see cref="Config_ArenaId_MismatchIsDetected"/>, which drives <c>AssertArenaIdsAgree</c> with a
    /// deliberately mismatched pair.
    ///
    /// (<c>src/AlphaLab.Web/wwwroot/appsettings.json</c> is deliberately excluded: it carries the plural
    /// <c>Arenas</c> registry — an ArenaEntry list with baseUrl — a different shape, and never opens the DB.)
    /// </summary>
    [Fact]
    public void Config_ArenaId_AgreesAcrossProcesses()
    {
        var repoRoot = FindRepoRoot();
        var paths = new[]
        {
            Path.Combine(repoRoot, "src", "AlphaLab.Api", "appsettings.json"),
            Path.Combine(repoRoot, "src", "AlphaLab.Worker", "appsettings.json"),
            Path.Combine(repoRoot, "tools", "Backfill", "appsettings.json"),
        };

        AssertArenaIdsAgree(paths); // no throw == the three processes resolve to the same arena/DB
    }

    /// <summary>
    /// The negative for the lock above. It drives the SAME guard method (<see cref="AssertArenaIdsAgree"/>)
    /// with a deliberately mismatched pair, so a regression in that method (wrong property, bad comparison,
    /// a silently-skipped file) fails HERE — it is not a LINQ tautology that re-implements the check over
    /// throwaway files and asserts <c>Distinct</c> works.
    /// </summary>
    [Fact]
    public void Config_ArenaId_MismatchIsDetected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "alphalab-arenaid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.json");
            var b = Path.Combine(dir, "b.json");
            File.WriteAllText(a, /*lang=json*/ """{ "Arena": { "Id": "sp500" } }""");
            File.WriteAllText(b, /*lang=json*/ """{ "Arena": { "Id": "sp100" } }""");

            var ex = Assert.Throws<InvalidOperationException>(() => AssertArenaIdsAgree([a, b]));
            Assert.Contains("Arena:Id disagrees", ex.Message, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Reads <c>Arena:Id</c> from each appsettings and throws (naming the disagreeing dir=id pairs) unless
    /// they are all equal. Extracted so the positive lock and the negative test exercise identical logic.
    /// </summary>
    private static void AssertArenaIdsAgree(IReadOnlyList<string> appsettingsPaths)
    {
        var ids = appsettingsPaths
            .Select(p => (Path: p, Id: ReadArenaId(p)))
            .ToList();

        if (ids.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            var detail = string.Join(", ", ids.Select(x =>
                $"{Path.GetFileName(Path.GetDirectoryName(x.Path))}={x.Id}"));
            throw new InvalidOperationException(
                $"Arena:Id disagrees across processes ({detail}) - each resolves the {{Arena.Id}} token from " +
                "its own appsettings, so byte-identical connection strings would still open different databases.");
        }
    }

    private static string ReadArenaId(string appsettingsPath)
    {
        Assert.True(File.Exists(appsettingsPath), $"missing {appsettingsPath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
        return doc.RootElement.GetProperty("Arena").GetProperty("Id").GetString()
            ?? throw new InvalidOperationException($"Arena:Id is null in {appsettingsPath}");
    }

    /// <summary>
    /// Finding 266: <c>Universe:Exclusions</c> is read by TWO processes for one purpose — the Backfill CLI
    /// SKIPS the symbols on ingest and the Worker's replay composition DENIES them from the roster
    /// (<c>ExclusionScopedMembershipRead</c>). If the two lists drift, the backfill could skip a name the
    /// replay still rosters (or the reverse), and a wrong-company symbol would leak back into replay. This
    /// locks the two consumers' lists to the same value. (The Api never reads it — it is a read/command
    /// boundary, so it is deliberately not in scope here.)
    /// </summary>
    [Fact]
    public void Config_UniverseExclusions_AgreeAcrossConsumers()
    {
        var repoRoot = FindRepoRoot();
        var worker = ReadUniverseExclusions(Path.Combine(repoRoot, "src", "AlphaLab.Worker", "appsettings.json"));
        var backfill = ReadUniverseExclusions(Path.Combine(repoRoot, "tools", "Backfill", "appsettings.json"));

        // Ordered, exact: the ingest-skip and the replay deny-list must agree symbol-for-symbol.
        Assert.Equal(worker, backfill);
    }

    /// <summary>
    /// The D24 daily ceiling must read the same in BOTH processes that consult it (v1.9.66, checkpoint 5.6).
    ///
    /// The Worker enforces the ceiling when it spends; the API refuses an FR-23 command with 503 when the
    /// day is already spent. Those are two readings of one number, and if they diverge the API accepts a
    /// command the Worker will then refuse to run — a queued job that fails for a reason the caller was
    /// told did not apply. Same class as the arena-id and universe-exclusion agreements above: a value
    /// duplicated across processes needs a test, not a convention.
    /// </summary>
    [Fact]
    public void Config_LlmDailyBudget_AgreesAcrossProcesses()
    {
        var repoRoot = FindRepoRoot();
        var api = ReadDailyBudget(Path.Combine(repoRoot, "src", "AlphaLab.Api", "appsettings.json"));
        var worker = ReadDailyBudget(Path.Combine(repoRoot, "src", "AlphaLab.Worker", "appsettings.json"));

        Assert.Equal(worker, api);
    }

    /// <summary>(MaxCostUsd, MaxCalls, MaxTokens) as committed. An absent section returns the bound
    /// defaults, so "neither file configures it" agrees rather than failing on a technicality.</summary>
    private static (double, int, int) ReadDailyBudget(string appsettingsPath)
    {
        Assert.True(File.Exists(appsettingsPath), $"missing {appsettingsPath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
        if (!doc.RootElement.TryGetProperty("Llm", out var llm) ||
            !llm.TryGetProperty("DailyBudget", out var b))
        {
            return (0.39, 10, 130_000);   // LlmDailyBudgetOptions' defaults (derived, D130)
        }

        return (
            b.TryGetProperty("MaxCostUsd", out var c) ? c.GetDouble() : 0.39,
            b.TryGetProperty("MaxCalls", out var n) ? n.GetInt32() : 10,
            b.TryGetProperty("MaxTokens", out var t) ? t.GetInt32() : 130_000);
    }

    /// <summary>
    /// FX-BudgetDerivation (D130): every derived spend cap equals its formula output from the ONE
    /// authored number, recomputed here from the COMMITTED appsettings — so a hand-edit of a derived cap
    /// fails the suite. Rounding is to the cent, as CONFIG_REFERENCE's D130 block states, so the fixture
    /// and the file cannot disagree about what "equals" means. The Api's copy of DailyBudget is held
    /// equal to the Worker's by Config_LlmDailyBudget_AgreesAcrossProcesses above.
    /// </summary>
    [Fact]
    public void FX_BudgetDerivation_DerivedCapsEqualTheirFormulas()
    {
        var repoRoot = FindRepoRoot();
        var worker = Path.Combine(repoRoot, "src", "AlphaLab.Worker", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(worker));
        var llm = doc.RootElement.GetProperty("Llm");
        var ai = doc.RootElement.GetProperty("Ai");

        var annual = llm.GetProperty("AnnualBudgetUsd").GetDecimal();
        var committed = annual * (1m - 0.15m);

        Assert.Equal(
            Math.Round(committed * 0.60m / 252m, 2),
            ai.GetProperty("Contestant").GetProperty("DailyBudgetUsd").GetDecimal());
        Assert.Equal(
            Math.Round(committed * 0.40m / 12m, 2),
            ai.GetProperty("Researcher").GetProperty("MonthlyBudgetUsd").GetDecimal());

        var budget = llm.GetProperty("DailyBudget");
        var maxCost = budget.GetProperty("MaxCostUsd").GetDecimal();
        Assert.Equal(Math.Round(committed / 252m * 1.15m, 2), maxCost);

        // MaxTokens = floor(MaxCostUsd / (mean uncached input rate / 1e6)) — finding 320's knob, enforced.
        var meanInputPerMTok = llm.GetProperty("Pricing").EnumerateObject()
            .Select(m => m.Value.GetProperty("InputPerMTok").GetDecimal())
            .Average();
        Assert.Equal(
            (int)Math.Floor(maxCost / (meanInputPerMTok / 1_000_000m)),
            budget.GetProperty("MaxTokens").GetInt32());

        // The token ceiling is >0 under DEFAULT config too — committed file and bound defaults agree
        // that the third D24 dimension is enforced, not disabled.
        Assert.True(budget.GetProperty("MaxTokens").GetInt32() > 0);
        Assert.True(new LlmDailyBudgetOptions().MaxTokens > 0);
    }

    /// <summary>
    /// D130/3-C: the shortlist size is bounded by the STRUCTURAL D24 scope cap, not the budget.
    /// Recomputes the budget-affordable N from the committed values and the MODELLED per-name tokens
    /// (72 in / 24 out — MASTER §23.2's layer table at 25 names; MODELLED, NOT MEASURED, replaced by
    /// analysis_cache actuals per D130's recalibration trigger) and asserts it clears the committed size:
    /// the budget does not bind at the authored annual, so the scope rail (25, D24/D127) is what binds —
    /// and that value is FROZEN per contestant generation into config_json, never re-read per session
    /// (FX-ShortlistSizeFrozenPerGeneration, Phase 6, when the generation machinery exists).
    /// </summary>
    [Fact]
    public void FX_ShortlistDerivation_TheScopeCapBinds_NotTheBudget()
    {
        var repoRoot = FindRepoRoot();
        var worker = Path.Combine(repoRoot, "src", "AlphaLab.Worker", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(worker));
        var llm = doc.RootElement.GetProperty("Llm");
        var ai = doc.RootElement.GetProperty("Ai");

        var daily = ai.GetProperty("Contestant").GetProperty("DailyBudgetUsd").GetDecimal();
        var opus = llm.GetProperty("Pricing").GetProperty("claude-opus-5");
        var batch = llm.GetProperty("BatchDiscountMultiplier").GetDecimal();

        var costPerNamePerDay =
            (72m * opus.GetProperty("InputPerMTok").GetDecimal()
             + 24m * opus.GetProperty("OutputPerMTok").GetDecimal())
            / 1_000_000m * batch;
        var affordable = (int)Math.Floor(daily / costPerNamePerDay);

        var committedSize = ai.GetProperty("Contestant").GetProperty("ShortlistSize").GetInt32();
        Assert.Equal(25, committedSize);            // the D24/D127 structural scope rail, pinned
        Assert.True(affordable >= committedSize,    // the budget does not bind at the authored annual
            $"budget affords only {affordable} names/day — below the scope cap {committedSize}; " +
            "the budget has become the binding constraint and D130's 3-C derivation must be re-read");
    }

    /// <summary>
    /// The D82/D112 Research numbers must read the same in BOTH processes (v1.9.70, finding 334).
    ///
    /// The Api gates proposals on MaxConcurrentCandidates (D112) and surfaces ForkBudgetPerYear with
    /// every 202; the Worker packs fork_budget_remaining from the same numbers (the cp-1.0 field). A
    /// divergence would have the seat told one budget and packed another — the same
    /// accepted-then-refused class as the Llm.DailyBudget agreement above.
    /// </summary>
    [Fact]
    public void Config_Research_AgreesAcrossProcesses()
    {
        var repoRoot = FindRepoRoot();
        var api = ReadResearch(Path.Combine(repoRoot, "src", "AlphaLab.Api", "appsettings.json"));
        var worker = ReadResearch(Path.Combine(repoRoot, "src", "AlphaLab.Worker", "appsettings.json"));

        Assert.Equal(worker, api);
    }

    /// <summary>(ForkBudgetPerYear, MaxConcurrentCandidates); an absent section returns the bound
    /// defaults, so "neither file configures it" agrees rather than failing on a technicality.</summary>
    private static (int, int) ReadResearch(string appsettingsPath)
    {
        Assert.True(File.Exists(appsettingsPath), $"missing {appsettingsPath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
        if (!doc.RootElement.TryGetProperty("Research", out var r)) return (6, 3);
        return (
            r.TryGetProperty("ForkBudgetPerYear", out var f) ? f.GetInt32() : 6,
            r.TryGetProperty("MaxConcurrentCandidates", out var m) ? m.GetInt32() : 3);
    }

    private static IReadOnlyList<string> ReadUniverseExclusions(string appsettingsPath)
    {
        Assert.True(File.Exists(appsettingsPath), $"missing {appsettingsPath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
        if (!doc.RootElement.TryGetProperty("Universe", out var universe) ||
            !universe.TryGetProperty("Exclusions", out var exclusions))
        {
            return [];   // an absent section is the empty-list default — and must be empty on BOTH sides
        }
        return exclusions.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "AlphaLab.slnx")) &&
               !File.Exists(Path.Combine(dir.FullName, "AlphaLab.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root (AlphaLab.slnx) not found");
    }
}
