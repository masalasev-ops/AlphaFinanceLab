using System.Text.Json;
using AlphaLab.Data;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Guards the "all three processes open the SAME file" invariant (DB_RELOCATION.md): the Api and
/// Worker appsettings.json <c>ConnectionStrings:AlphaLab</c> must equal
/// <see cref="DbPathResolver.DefaultConnectionString"/> (which the EF design-time factory uses). These
/// are the three edit spots that must move together when the DB is relocated.
/// </summary>
public sealed class ConfigConsistencyTests
{
    [Theory]
    [InlineData("AlphaLab.Api")]
    [InlineData("AlphaLab.Worker")]
    public void Config_ConnectionString_EqualsResolverDefault(string project)
    {
        var repoRoot = FindRepoRoot();
        var appsettings = Path.Combine(repoRoot, "src", project, "appsettings.json");
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
