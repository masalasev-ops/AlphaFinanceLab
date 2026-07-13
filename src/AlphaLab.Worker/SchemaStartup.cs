using System.Data;
using AlphaLab.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>
/// The shared schema-application step (v1.9.6). Registered UNCONDITIONALLY before the Quartz
/// hosted service and before the OnDemand runner, so it runs identically in both modes and its
/// StartAsync completes before anything reads the store (IHostedService.StartAsync runs in
/// registration order — do not reorder).
///
/// Phase 0: applies the pending InitialCreate migration (MigrateAsync).
/// Phase 1: this becomes fail-fast — log pending migrations + non-zero exit; schema then applies
///          ONLY through tools/migrate.ps1 (snapshot first, rule 14). See the marker below.
///
/// After a successful migrate it establishes WAL (finding 118): PRAGMA journal_mode=WAL on the
/// context connection, reads the mode back, logs it (arena-tagged), and treats anything other
/// than 'wal' as a startup failure (log + non-zero exit + StopApplication). WAL is persistent
/// per file, so this also upgrades a pre-existing rollback-journal DB on its next Worker start.
/// The API must NEVER run this pragma (a reader must not convert the store).
/// </summary>
public sealed class SchemaStartup(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ArenaOptions arena,
    ILogger<SchemaStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var arenaScope = logger.BeginArenaScope(arena);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlphaLabDbContext>();

        try
        {
            // --- Phase 0: apply pending migrations. ---
            // Phase 1: replace MigrateAsync with a pending-migration fail-fast (log + non-zero exit);
            //          schema application then goes only through tools/migrate.ps1.
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Schema up to date (applied any pending migrations).");

            var mode = await SetAndReadJournalModeAsync(db, cancellationToken);
            logger.LogInformation("journal_mode={Mode}", mode ?? "(null)");

            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                Fail($"Expected journal_mode=wal after schema startup but the store reported '{mode ?? "(null)"}'.");
            }
        }
        catch (SchemaStartupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail($"Schema startup failed: {ex.Message}", ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<string?> SetAndReadJournalModeAsync(AlphaLabDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        var result = await command.ExecuteScalarAsync(ct);
        return result?.ToString();
    }

    private void Fail(string message, Exception? inner = null)
    {
        logger.LogCritical(inner, "{Message} Worker will exit.", message);
        Environment.ExitCode = 1;
        lifetime.StopApplication();
        throw new SchemaStartupException(message, inner);
    }
}
