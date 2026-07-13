using AlphaLab.Data;
using AlphaLab.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Tests;

public class SchemaStartupTests
{
    [Fact]
    public async Task R1_SchemaStartup_EnablesWal()
    {
        var dbPath = TempDb();
        try
        {
            await using var provider = BuildProvider(dbPath);
            await NewSchemaStartup(provider).StartAsync(CancellationToken.None);

            // A FRESH connection must report wal (WAL is persistent per file).
            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal("wal", mode, ignoreCase: true);
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public async Task StartAsync_AppliesInitialCreate_CreatesFiveInfraTables()
    {
        var dbPath = TempDb();
        try
        {
            await using var provider = BuildProvider(dbPath);
            await NewSchemaStartup(provider).StartAsync(CancellationToken.None);

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '\_\_%' ESCAPE '\' ORDER BY name;";
            var tables = new List<string>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            }

            Assert.Equal(
                new[] { "catchup_log", "config", "jobs", "runs", "worker_state" },
                tables);
        }
        finally { TryDelete(dbPath); }
    }

    private static ServiceProvider BuildProvider(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" });
        services.AddAlphaLabData($"Data Source={dbPath}", "sp500", ensureDirectory: true);
        return services.BuildServiceProvider();
    }

    private static SchemaStartup NewSchemaStartup(IServiceProvider provider) =>
        new(provider,
            new RecordingLifetime(),
            provider.GetRequiredService<ArenaOptions>(),
            provider.GetRequiredService<ILogger<SchemaStartup>>());

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), "alphalab-wal-" + Guid.NewGuid().ToString("N") + ".db");

    private static void TryDelete(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public bool StopApplicationCalled { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopApplicationCalled = true;
    }
}
