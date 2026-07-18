using System.Data;
using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>What the per-launch backup did. <see cref="Created"/> ⇒ a fresh dated copy was written this
/// launch; false ⇒ today's copy already existed (a same-day re-launch) and was skipped.</summary>
public sealed record LocalBackupResult(bool Created, string? BackupPath, int Pruned);

/// <summary>
/// Step 4 of the D72 launch order (checkpoint 2.12) / RUNBOOK §3: the per-launch LOCAL backup. It runs after
/// catch-up + job drain, so the store is quiescent (no open write transaction). This is the on-drive
/// convenience snapshot; the OFF-machine copy stays a manual operator action (mandatory from the first
/// Phase-2 write, but the lab never ships credentials to reach an off-machine target).
///
/// Sequence: PRAGMA wal_checkpoint(TRUNCATE) folds the WAL into the main file so a plain file copy is a
/// consistent snapshot → copy to &lt;DbBase&gt;\{arena}\backups\alphalab-{date}.db (skip if today's exists) →
/// prune copies older than Ops.BackupRetentionDays. Pruning is by the DATE IN THE FILENAME (deterministic),
/// not file mtime. The backup directory derives from the resolved Data Source via the pure
/// <see cref="DbPathResolver.BackupDirectory"/> — arena-namespaced by construction (rule 23).
/// </summary>
public sealed class LocalBackup(
    IServiceScopeFactory scopeFactory,
    OpsOptions ops,
    TimeProvider clock,
    ArenaOptions arena,
    ILogger<LocalBackup> logger)
{
    public Task<LocalBackupResult> BackupAsync(CancellationToken ct = default)
    {
        using var arenaScope = logger.BeginArenaScope(arena);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlphaLabDbContext>();

        var resolvedCs = db.Database.GetDbConnection().ConnectionString;
        var dbPath = DbPathResolver.GetDataSourcePath(resolvedCs);
        var backupDir = DbPathResolver.BackupDirectory(resolvedCs);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var backupFile = DbPathResolver.BackupFilePath(resolvedCs, today);

        if (!File.Exists(dbPath))
        {
            // Nothing to back up (e.g. a store that was never created). Not fatal — log and move on.
            logger.LogWarning("Local backup: store '{DbPath}' does not exist — skipping.", dbPath);
            return Task.FromResult(new LocalBackupResult(false, null, 0));
        }

        Directory.CreateDirectory(backupDir);

        if (File.Exists(backupFile))
        {
            // A second launch the same day: today's snapshot is already taken. Still prune (retention may
            // have advanced), but do not overwrite (idempotent per day).
            var prunedOnly = PruneOldBackups(backupDir, today, ops.BackupRetentionDays);
            logger.LogInformation("Local backup: today's copy already exists ({File}) — skipped; pruned {Pruned}.", backupFile, prunedOnly);
            return Task.FromResult(new LocalBackupResult(false, backupFile, prunedOnly));
        }

        Checkpoint(db);
        File.Copy(dbPath, backupFile, overwrite: false);

        var pruned = PruneOldBackups(backupDir, today, ops.BackupRetentionDays);
        logger.LogInformation("Local backup: wrote {File}; pruned {Pruned} copy(ies) older than {Days} day(s).",
            backupFile, pruned, ops.BackupRetentionDays);
        return Task.FromResult(new LocalBackupResult(true, backupFile, pruned));
    }

    // Fold the WAL into the main .db so the file copy is a complete, consistent snapshot. A non-zero busy
    // flag means a reader blocked a full truncate — logged, not fatal (the copy is still consistent up to the
    // last committed frame folded in).
    private void Checkpoint(AlphaLabDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var busy = reader.GetInt32(0);
            if (busy != 0) logger.LogWarning("Local backup: wal_checkpoint(TRUNCATE) returned busy={Busy} — a reader was active.", busy);
        }
    }

    private int PruneOldBackups(string backupDir, DateOnly today, int retentionDays)
    {
        if (!Directory.Exists(backupDir)) return 0;
        var cutoff = today.AddDays(-Math.Max(0, retentionDays));
        var pruned = 0;
        foreach (var file in Directory.EnumerateFiles(backupDir, "alphalab-*.db"))
        {
            if (TryParseBackupDate(Path.GetFileName(file), out var d) && d < cutoff)
            {
                try { File.Delete(file); pruned++; }
                catch (IOException ex) { logger.LogWarning(ex, "Local backup: could not prune {File}.", file); }
            }
        }
        return pruned;
    }

    /// <summary>Parse the yyyy-MM-dd out of "alphalab-{date}.db". Pure — no filesystem access.</summary>
    public static bool TryParseBackupDate(string fileName, out DateOnly date)
    {
        date = default;
        const string prefix = "alphalab-";
        const string suffix = ".db";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var datePart = fileName[prefix.Length..^suffix.Length];
        return DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
