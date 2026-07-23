using System.Text.Json;
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
