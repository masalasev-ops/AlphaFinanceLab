using AlphaLab.Data;

namespace AlphaLab.Data.Tests;

public class DbPathResolverTests
{
    [Fact]
    public void FR37_ArenaNamespacedDbPath()
    {
        const string cs = "Data Source=E:\\AlphaLabDatabase\\{Arena.Id}\\alphalab.db";

        var sp500 = DbPathResolver.GetDataSourcePath(DbPathResolver.ResolvePath(cs, "sp500"));
        var sp100 = DbPathResolver.GetDataSourcePath(DbPathResolver.ResolvePath(cs, "sp100"));

        Assert.DoesNotContain("{Arena.Id}", sp500);
        Assert.EndsWith(Path.Combine("sp500", "alphalab.db"), sp500);
        // Two configs differing only in Arena.Id resolve to distinct, non-colliding paths (FR-37 / D71).
        Assert.EndsWith(Path.Combine("sp100", "alphalab.db"), sp100);
        Assert.NotEqual(sp500, sp100);
    }

    [Fact]
    public void ResolvePath_IsPure_DoesNotTouchTheFilesystem()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "alphalab-pure-" + Guid.NewGuid().ToString("N"));
        var cs = $"Data Source={tempBase}\\{{Arena.Id}}\\alphalab.db";

        var sp500 = DbPathResolver.GetDataSourcePath(DbPathResolver.ResolvePath(cs, "sp500"));
        var sp100 = DbPathResolver.GetDataSourcePath(DbPathResolver.ResolvePath(cs, "sp100"));

        // Purely functional: neither arena's directory is created, and the two arenas are distinct.
        Assert.False(Directory.Exists(Path.Combine(tempBase, "sp500")));
        Assert.False(Directory.Exists(Path.Combine(tempBase, "sp100")));
        Assert.NotEqual(sp500, sp100);
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

    [Fact]
    public void ResolvePath_ProducesOsNativeAbsolutePath_NoForeignSeparator()
    {
        // The portable default (token form, forward slashes in the template) must resolve to
        // a fully-qualified path anchored at the known folder, with NO separator from the
        // other OS surviving. This is the property that makes a Windows-authored config valid
        // on a Linux VM.
        const string cs = "Data Source={LocalAppData}/AlphaLabDatabase/{Arena.Id}/alphalab.db";

        var path = DbPathResolver.GetDataSourcePath(DbPathResolver.ResolvePath(cs, "sp500"));

        Assert.True(Path.IsPathFullyQualified(path), $"expected an absolute path, got '{path}'");
        var foreignSeparator = Path.DirectorySeparatorChar == '\\' ? '/' : '\\';
        Assert.DoesNotContain(foreignSeparator.ToString(), path);
        Assert.EndsWith(Path.Combine("AlphaLabDatabase", "sp500", "alphalab.db"), path);
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

    // ---- D72 backup-path helpers (checkpoint 2.12): pure, arena-namespaced, no filesystem access ----

    [Fact]
    public void BackupDirectory_IsAnArenaNamespacedSiblingOfTheStore()
    {
        const string cs = "Data Source=E:\\AlphaLabDatabase\\{Arena.Id}\\alphalab.db";

        var sp500 = DbPathResolver.BackupDirectory(DbPathResolver.ResolvePath(cs, "sp500"));
        var sp100 = DbPathResolver.BackupDirectory(DbPathResolver.ResolvePath(cs, "sp100"));

        Assert.EndsWith(Path.Combine("sp500", "backups"), sp500);
        // Namespaced per arena — no cross-arena backup bleed (rule 23).
        Assert.EndsWith(Path.Combine("sp100", "backups"), sp100);
        Assert.NotEqual(sp500, sp100);
    }

    [Fact]
    public void BackupFilePath_IsDatedAndDoesNotTouchTheFilesystem()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "alphalab-bkp-" + Guid.NewGuid().ToString("N"));
        var resolved = DbPathResolver.ResolvePath($"Data Source={tempBase}\\{{Arena.Id}}\\alphalab.db", "sp500");

        var file = DbPathResolver.BackupFilePath(resolved, new DateOnly(2026, 7, 17));

        Assert.EndsWith(Path.Combine("sp500", "backups", "alphalab-2026-07-17.db"), file);
        // Pure: nothing was created on disk.
        Assert.False(Directory.Exists(Path.Combine(tempBase, "sp500", "backups")));
    }
}
