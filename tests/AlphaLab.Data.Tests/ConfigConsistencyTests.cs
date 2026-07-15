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
