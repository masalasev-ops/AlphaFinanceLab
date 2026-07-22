using System.Text.Json.Nodes;
using AlphaLab.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Api.Tests;

/// <summary>
/// Boots the API against an isolated, migrated temp-SQLite arena (instead of the appsettings arena file),
/// so the Phase-3 read/command endpoints have a real schema to hit.
///
/// The swap is done through CONFIG, not through a post-hoc DbContext re-registration (v1.9.36): Program.cs
/// resolves ConnectionStrings:AlphaLab and hands it to AddAlphaLabData at composition-root time, where the
/// D-rule-10 absolute-path guard runs — long before ConfigureTestServices could replace anything. Booting
/// the test host on the COMMITTED connection string therefore drags the production store's path into every
/// API test, and the committed value (<c>E:/AlphaLabDatabase/...</c>) is fully qualified on Windows but
/// RELATIVE on POSIX, so the guard failed all 13 tests on the Linux leg while the Windows leg stayed green.
///
/// Program.cs clears its config sources and re-reads appsettings.json from ContentRootPath, so the seam that
/// actually works is the content root: this factory writes a temp copy of the committed Api appsettings.json
/// with ONLY ConnectionStrings:AlphaLab repointed at its temp arena (every other production value is kept
/// verbatim, so tests still exercise the real config), and points the host at that directory. The temp path
/// is absolute on both platforms, so the guard passes on its own terms rather than being bypassed.
/// </summary>
public sealed class ApiArenaFactory : WebApplicationFactory<Program>
{
    private readonly string _contentRoot =
        Path.Combine(Path.GetTempPath(), "alphalab-api-" + Guid.NewGuid().ToString("N"));

    public string DbPath { get; }

    public ApiArenaFactory()
    {
        Directory.CreateDirectory(_contentRoot);
        DbPath = Path.Combine(_contentRoot, "alphalab.db");
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"), TestAppSettings(DbPath));

        using var db = Open();
        db.Database.Migrate();
    }

    // The ONLY override: the host reads its appsettings.json from here, so it opens the temp arena.
    // appsettings.Secrets.json is deliberately NOT copied — a reader needs no keys, and secrets never
    // get duplicated into a temp directory (hard rule 11).
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseContentRoot(_contentRoot);

    public AlphaLabDbContext Open() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={DbPath}").Options);

    /// <summary>The committed Api appsettings.json with ConnectionStrings:AlphaLab repointed at <paramref name="dbPath"/>.</summary>
    private static string TestAppSettings(string dbPath)
    {
        var committed = Path.Combine(FindRepoRoot(), "src", "AlphaLab.Api", "appsettings.json");
        var json = JsonNode.Parse(File.ReadAllText(committed))!;
        json["ConnectionStrings"]!["AlphaLab"] = $"Data Source={dbPath}";
        return json.ToJsonString();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaLab.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root (AlphaLab.slnx) not found");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best effort */ }
    }
}
