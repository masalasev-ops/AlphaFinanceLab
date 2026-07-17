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
/// SCHEMA IS NEVER APPLIED HERE (rule 14; v1.9.17 finding A). This step VERIFIES the schema is
/// current and fails the host if it is not — it does not migrate. Applying schema is the sole job
/// of tools/migrate.ps1, which snapshots the exact file it is about to migrate FIRST.
///
/// Why this matters now, and why it is not merely tidy: through Phase 1 this class called
/// MigrateAsync, which was harmless only because nothing was ever pending — the store was
/// migrated by tools/migrate.ps1 before the Worker ever saw it. The moment a Phase-2 migration
/// ships un-applied, an ordinary evening Worker launch would silently migrate the operator's live
/// store with NO pre-migration snapshot, which is exactly what RUNBOOK §2 forbids ("never run
/// `dotnet ef database update` directly against a non-empty DB") and what rule 14 exists to
/// prevent. Phase 2 is also the first phase whose tables hold the lab's OWN output (trades,
/// decisions, equity_curve) — rows no provider can re-fetch. An auto-migrate here is a
/// silent-data-loss path, so it fails closed instead (hard rule 10).
///
/// BUILD 0.4 specified this fail-fast "from Phase 1"; it was never shipped. This is that.
///
/// After verifying the schema it establishes WAL (finding 118): PRAGMA journal_mode=WAL on the
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
            // --- Verify, never apply (rule 14). ---
            // A pending migration is an OPERATOR action, not a side effect of launching the Worker:
            // tools/migrate.ps1 snapshots the exact file before touching it. Refuse and say so.
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                Fail(
                    $"The store has {pending.Count} pending migration(s): {string.Join(", ", pending)}. " +
                    $"Schema is applied ONLY by the snapshot-first path (rule 14) — run: " +
                    $"pwsh tools/migrate.ps1 -Arena {arena.Id}");
            }

            logger.LogInformation("Schema verified up to date (no pending migrations).");

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
