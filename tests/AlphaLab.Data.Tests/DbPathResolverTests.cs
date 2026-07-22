using AlphaLab.Data;
using Microsoft.Data.Sqlite;

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
    public void FR37_ResolvePath_NormalizesSeparatorsToTheRunningOs()
    {
        // The property that makes ONE config string valid on every OS: no separator from the other
        // platform survives resolution.
        //
        // Assert on the ResolvePath OUTPUT, never on GetDataSourcePath's — the latter applies
        // Path.GetFullPath, which on Windows converts '/' to '\' BY ITSELF and would make this test
        // pass with the normalization deleted. The template deliberately mixes '\' and '/' so that
        // one of them is foreign on whichever platform runs the suite; a single-separator template
        // is native somewhere and cannot fail there.
        const string cs = @"Data Source={LocalAppData}/AlphaLabDatabase\{Arena.Id}/alphalab.db";

        var resolved = DbPathResolver.ResolvePath(cs, "sp500");
        var dataSource = new SqliteConnectionStringBuilder(resolved).DataSource;

        var foreign = Path.DirectorySeparatorChar == '\\' ? '/' : '\\';
        Assert.DoesNotContain(foreign.ToString(), dataSource);
        Assert.EndsWith(Path.Combine("AlphaLabDatabase", "sp500", "alphalab.db"), dataSource);
        // ...and it is a real absolute path anchored at the known folder, not a filename with separators in it.
        Assert.True(Path.IsPathFullyQualified(dataSource), $"expected an absolute path, got '{dataSource}'");
    }

    [Fact]
    public void FR37_ResolvePath_LeavesUriDataSourceUntouched()
    {
        // SQLite URI data sources are URIs, not paths: the grammar mandates '/', so normalizing
        // separators would corrupt them on Windows (mode/cache query strings included).
        const string cs = "Data Source=file:/tmp/alphalab/{Arena.Id}/alphalab.db?mode=ro";

        var dataSource = new SqliteConnectionStringBuilder(DbPathResolver.ResolvePath(cs, "sp500")).DataSource;

        Assert.Equal("file:/tmp/alphalab/sp500/alphalab.db?mode=ro", dataSource);
    }

    [Fact]
    public void FR37_RequireAbsoluteStorePath_RejectsARelativeStore()
    {
        // A relative Data Source means Worker/Api/Backfill each open a DIFFERENT database under their
        // own working directory - silently, each freshly created and empty (DB_RELOCATION.md §1).
        // Fail closed (rule 10) rather than let GetFullPath mask it by rooting at the CWD.
        var resolved = DbPathResolver.ResolvePath("Data Source=alphalab/{Arena.Id}/alphalab.db", "sp500");

        var ex = Assert.Throws<InvalidOperationException>(() => DbPathResolver.RequireAbsoluteStorePath(resolved));
        Assert.Contains("not absolute", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FR37_TheShippedDefault_IsWindowsAnchored_AndFailsClosedElsewhere()
    {
        // The committed four-spots value is `E:/AlphaLabDatabase/{Arena.Id}/alphalab.db`. Separator
        // normalization (v1.9.36) makes that ONE STRING PARSE the same everywhere - it does NOT make a
        // Windows drive letter meaningful on POSIX. So the honest contract is platform-conditional, and
        // this test pins it rather than pretending the committed default is portable:
        //   Windows -> absolute, the guard passes, the lab opens E:\AlphaLabDatabase\sp500\alphalab.db.
        //   POSIX   -> `E:/...` is RELATIVE, so the guard THROWS instead of silently giving the Worker,
        //              the Api, and the Backfill CLI one empty database each under their own CWDs.
        // That throw is the point: a cloud lift-and-shift that forgets to repoint the four spots fails
        // loudly (DB_RELOCATION.md §5) instead of forking the store.
        var resolved = DbPathResolver.ResolvePath(DbPathResolver.DefaultConnectionString, "sp500");

        if (OperatingSystem.IsWindows())
        {
            DbPathResolver.RequireAbsoluteStorePath(resolved);   // no throw
        }
        else
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DbPathResolver.RequireAbsoluteStorePath(resolved));
            Assert.Contains("not absolute", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FR37_ThePortableTokenForm_IsAbsoluteOnEveryPlatform()
    {
        // The counterpart to the test above, and the actual cloud-move recipe: the {LocalAppData} token
        // resolves through the known-folders API to an absolute location on BOTH platforms
        // (%LOCALAPPDATA% on Windows, ~/.local/share on Linux), so this form needs no per-OS edit.
        var resolved = DbPathResolver.ResolvePath(
            "Data Source={LocalAppData}/AlphaLabDatabase/{Arena.Id}/alphalab.db", "sp500");

        DbPathResolver.RequireAbsoluteStorePath(resolved);   // no throw, on any OS
    }

    [Fact]
    public void FR37_Resolve_RefusesToCreateAStoreUnderTheWorkingDirectory()
    {
        // The writer path must refuse BEFORE EnsureDirectoryExists runs - otherwise a relative config
        // silently creates a stray store beside whatever CWD the process happened to start in.
        var relative = Path.Combine("alphalab-relative-" + Guid.NewGuid().ToString("N"), "{Arena.Id}");

        Assert.Throws<InvalidOperationException>(
            () => DbPathResolver.Resolve($"Data Source={relative}/alphalab.db", "sp500"));
        Assert.False(Directory.Exists(Path.GetFullPath(relative.Replace("{Arena.Id}", "sp500"))));
    }

    [Fact]
    public void FR37_ResolvePath_MalformedConnectionString_FailsClosedWithADiagnosis()
    {
        // A typo in ConnectionStrings:AlphaLab must name the key and the offending value, not surface
        // as a bare 'Keyword not supported' from deep inside the SQLite builder.
        var ex = Assert.Throws<InvalidOperationException>(
            () => DbPathResolver.ResolvePath("Data Sorce=E:/x/{Arena.Id}/alphalab.db", "sp500"));

        Assert.Contains("ConnectionStrings:AlphaLab", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DB_RELOCATION", ex.Message, StringComparison.Ordinal);
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
