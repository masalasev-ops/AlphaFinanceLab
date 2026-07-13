using AlphaLab.Data;

namespace AlphaLab.Data.Tests;

public class DbPathResolverTests
{
    [Fact]
    public void FR37_ArenaNamespacedDbPath()
    {
        const string cs = "Data Source=E:\\AlphaLabDatabase\\{Arena.Id}\\alphalab.db";

        var resolved = DbPathResolver.ResolvePath(cs, "sp500");

        Assert.DoesNotContain("{Arena.Id}", resolved);
        var path = DbPathResolver.GetDataSourcePath(resolved);
        Assert.EndsWith(Path.Combine("sp500", "alphalab.db"), path);
    }

    [Fact]
    public void ResolvePath_IsPure_DoesNotTouchTheFilesystem()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "alphalab-pure-" + Guid.NewGuid().ToString("N"));
        var cs = $"Data Source={tempBase}\\{{Arena.Id}}\\alphalab.db";

        _ = DbPathResolver.ResolvePath(cs, "sp500");

        Assert.False(Directory.Exists(Path.Combine(tempBase, "sp500")));
    }

    [Fact]
    public void Resolve_CreatesTheStoreDirectory()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "alphalab-resolve-" + Guid.NewGuid().ToString("N"));
        var cs = $"Data Source={tempBase}\\{{Arena.Id}}\\alphalab.db";
        try
        {
            _ = DbPathResolver.Resolve(cs, "sp500");
            Assert.True(Directory.Exists(Path.Combine(tempBase, "sp500")));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, recursive: true);
        }
    }

    [Fact]
    public void ResolvePath_ReplacesLocalAppDataViaKnownFolder()
    {
        const string cs = "Data Source={LocalAppData}\\AlphaLab\\{Arena.Id}\\alphalab.db";

        var resolved = DbPathResolver.ResolvePath(cs, "sp500");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Contains(localAppData, resolved);
        Assert.DoesNotContain("{LocalAppData}", resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolvePath_RejectsBlankArena(string arena)
    {
        Assert.Throws<ArgumentException>(() => DbPathResolver.ResolvePath("Data Source=x", arena));
    }

    [Fact]
    public void FR37_DefaultConnectionString_HasArenaTokenAndDataSource()
    {
        Assert.Contains("{Arena.Id}", DbPathResolver.DefaultConnectionString);
        Assert.Contains("Data Source=", DbPathResolver.DefaultConnectionString);

        // It resolves to an arena-namespaced path with the token replaced.
        var resolved = DbPathResolver.ResolvePath(DbPathResolver.DefaultConnectionString, "sp500");
        Assert.DoesNotContain("{Arena.Id}", resolved);
        Assert.EndsWith(Path.Combine("sp500", "alphalab.db"), DbPathResolver.GetDataSourcePath(resolved));
    }
}
