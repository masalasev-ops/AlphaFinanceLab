using AlphaLab.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Api.Tests;

/// <summary>
/// Boots the API against an isolated, migrated temp-SQLite arena (instead of the appsettings arena file),
/// so the Phase-3 read/command endpoints have a real schema to hit. Program.cs clears its config sources,
/// so the DB is swapped via ConfigureTestServices (replace the DbContext registration) rather than config.
/// </summary>
public sealed class ApiArenaFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), "alphalab-api-" + Guid.NewGuid().ToString("N") + ".db");

    public ApiArenaFactory()
    {
        using var db = Open();
        db.Database.Migrate();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            foreach (var d in services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<AlphaLabDbContext>) ||
                d.ServiceType == typeof(AlphaLabDbContext)).ToList())
            {
                services.Remove(d);
            }
            services.AddDbContext<AlphaLabDbContext>(o => o.UseSqlite($"Data Source={DbPath}"));
        });
    }

    public AlphaLabDbContext Open() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={DbPath}").Options);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { if (File.Exists(DbPath + suffix)) File.Delete(DbPath + suffix); } catch { /* best effort */ }
    }
}
