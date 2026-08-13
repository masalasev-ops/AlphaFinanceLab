using AlphaLab.Data;
using AlphaLab.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// SchemaStartup VERIFIES the schema; it never applies it (rule 14; v1.9.17 finding A).
///
/// The contract these tests pin: a Worker launch against a store with pending migrations must
/// fail closed WITHOUT touching the schema. Through Phase 1 this class called MigrateAsync, which
/// was harmless only because nothing was ever pending. Phase 2 ships migrations AND is the first
/// phase whose tables hold rows no provider can re-fetch (trades, decisions, equity_curve), so an
/// auto-migrate here would silently migrate the operator's live store with no pre-migration
/// snapshot — exactly what RUNBOOK §2 forbids.
/// </summary>
public class SchemaStartupTests
{
    [Fact]
    public async Task SchemaStartup_PendingMigrations_FailsClosed_AndAppliesNothing()
    {
        var dbPath = TempDb();
        var previousExitCode = Environment.ExitCode;
        try
        {
            // An empty store: every migration is pending. This is the shape of the hazard — an
            // ordinary evening launch against a store that migrate.ps1 has not yet touched.
            await using var provider = BuildProvider(dbPath);
            var lifetime = new RecordingLifetime();

            var ex = await Assert.ThrowsAsync<SchemaStartupException>(
                () => NewSchemaStartup(provider, lifetime).StartAsync(CancellationToken.None));

            // The message must name the sanctioned path, or the operator is stuck.
            Assert.Contains("pending migration", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("migrate.ps1", ex.Message);
            Assert.Contains("sp500", ex.Message);

            Assert.True(lifetime.StopApplicationCalled);
            Assert.Equal(1, Environment.ExitCode);

            // The point of the whole change: it did NOT migrate. Zero user tables.
            Assert.Empty(await UserTablesAsync(dbPath));
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task R1_SchemaStartup_EnablesWal()
    {
        var dbPath = TempDb();
        try
        {
            await MigrateAsIfByMigratePs1Async(dbPath);

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
    public async Task SchemaStartup_SchemaCurrent_Verifies_AndDoesNotStopTheHost()
    {
        var dbPath = TempDb();
        try
        {
            await MigrateAsIfByMigratePs1Async(dbPath);

            await using var provider = BuildProvider(dbPath);
            var lifetime = new RecordingLifetime();
            await NewSchemaStartup(provider, lifetime).StartAsync(CancellationToken.None);

            // The happy path: schema is current, so the Worker proceeds to do its actual work.
            Assert.False(lifetime.StopApplicationCalled);

            // 45 tables — and the number is in ONE place, the assertion. This lead-in said "41" while the
            // assertion below said 43, the second instance of finding 444 (a count stated in prose drifts
            // from the count that is checked; the first was SchemaFidelityTests' method NAME). The
            // breakdown is kept because it says WHERE the tables came from, which is the part prose is
            // actually good for; the total is not restated.
            // Phase-0 infra(5) + Phase-1 data(9) + data_quality_flags(1) + ledger(8) + regime(2)
            // + the Phase-3 honest-arena tables(9): control_populations, control_equity, trials_registry,
            // power_reports, go_live_log, allocation_log, overfitting_checks, overfitting_status,
            // journal_entries + the Phase-3.5 D90 position_snapshots(1) + the Phase-4 M5
            // replay_regime_outcomes(1) + the Phase-4.5 M6 Signal Library(2): signals + signal_ic
            // + the Phase-5 M7 LLM tables(3): analysis_cache, llm_budget_log, news_items
            // + the Phase-5 M8 AI-seat tables(2): ai_context_packs, ai_decisions
            // + the Phase-6 M13 factor tables(2): factor_returns, factor_refresh_log (D41, checkpoint 6.6).
            var tables = await UserTablesAsync(dbPath);
            Assert.Equal(45, tables.Count);
            Assert.Contains("trades", tables);
            Assert.Contains("equity_curve", tables);
            Assert.Contains("regime_labels", tables);
            Assert.Contains("control_populations", tables);
            Assert.Contains("power_reports", tables);
            Assert.Contains("position_snapshots", tables);
            Assert.Contains("replay_regime_outcomes", tables);
            Assert.Contains("signals", tables);
            Assert.Contains("signal_ic", tables);
            Assert.Contains("analysis_cache", tables);
            Assert.Contains("llm_budget_log", tables);
            Assert.Contains("news_items", tables);
            Assert.Contains("factor_returns", tables);
            Assert.Contains("factor_refresh_log", tables);
            Assert.Contains("ai_context_packs", tables);
            Assert.Contains("ai_decisions", tables);
        }
        finally { TryDelete(dbPath); }
    }

    /// <summary>Stand in for the operator running tools/migrate.ps1 (which snapshots first, then
    /// applies). SchemaStartup itself must never do this — that is the whole point.</summary>
    private static async Task MigrateAsIfByMigratePs1Async(string dbPath)
    {
        await using var provider = BuildProvider(dbPath);
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AlphaLabDbContext>().Database.MigrateAsync();
    }

    private static async Task<List<string>> UserTablesAsync(string dbPath)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '\_\_%' ESCAPE '\' ORDER BY name;";
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static ServiceProvider BuildProvider(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" });
        services.AddAlphaLabData($"Data Source={dbPath}", "sp500", ensureDirectory: true);
        return services.BuildServiceProvider();
    }

    private static SchemaStartup NewSchemaStartup(IServiceProvider provider, RecordingLifetime? lifetime = null) =>
        new(provider,
            lifetime ?? new RecordingLifetime(),
            provider.GetRequiredService<ArenaOptions>(),
            provider.GetRequiredService<ILogger<SchemaStartup>>());

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), "alphalab-wal-" + Guid.NewGuid().ToString("N") + ".db");

    private static void TryDelete(string dbPath)
    {
        // P20: process-global; safe ONLY because parallelization is disabled assembly-wide.
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
