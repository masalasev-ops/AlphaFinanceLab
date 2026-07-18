using System.Data;
using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>What the per-launch backup did. <see cref="Created"/> ⇒ a fresh dated copy was written this
/// launch; false ⇒ today's copy already existed (a same-day re-launch) and was skipped, OR the step failed
/// closed (<see cref="Failed"/>) and wrote nothing.</summary>
public sealed record LocalBackupResult(bool Created, string? BackupPath, int Pruned, string? FailureReason = null)
{
    /// <summary>The backup ABORTED (rule 10) — no file was written this launch. Distinct from the benign
    /// same-day skip, where today's copy already exists.</summary>
    public bool Failed => FailureReason is not null;
}

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
///
/// A BUSY checkpoint fails CLOSED (v1.9.20 finding MM). The Api running as a separate reader process is a
/// supported topology, so a reader can hold the WAL while we checkpoint: a copy taken then can silently
/// omit recently committed transactions (or catch a partially applied checkpoint). Rule 10: retry the
/// checkpoint a small bounded number of times; if still busy, abort loudly and write NO file — a backup of
/// unknown integrity is worse than a missing one with a visible error.
/// </summary>
public sealed class LocalBackup(
    IServiceScopeFactory scopeFactory,
    OpsOptions ops,
    TimeProvider clock,
    ArenaOptions arena,
    ILogger<LocalBackup> logger)
{
    // Bounded checkpoint retry (finding MM): the common blocker (an Api read mid-flight) clears in
    // milliseconds, so each attempt waits at most CheckpointBusyWaitSeconds on SQLite's busy handler and we
    // retry a few times with a short pause. Anything that survives ~3.6s of retrying deserves a loud abort,
    // not a longer wait.
    private const int CheckpointAttempts = 3;
    private const int CheckpointBusyWaitSeconds = 1;
    private static readonly TimeSpan CheckpointRetryDelay = TimeSpan.FromMilliseconds(200);

    public async Task<LocalBackupResult> BackupAsync(CancellationToken ct = default)
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
            return new LocalBackupResult(false, null, 0);
        }

        Directory.CreateDirectory(backupDir);

        if (File.Exists(backupFile))
        {
            // A second launch the same day: today's snapshot is already taken. Still prune (retention may
            // have advanced), but do not overwrite (idempotent per day).
            var prunedOnly = PruneOldBackups(backupDir, today, ops.BackupRetentionDays);
            logger.LogInformation("Local backup: today's copy already exists ({File}) — skipped; pruned {Pruned}.", backupFile, prunedOnly);
            return new LocalBackupResult(false, backupFile, prunedOnly);
        }

        var busy = await CheckpointWithRetryAsync(db, ct).ConfigureAwait(false);
        if (busy != 0)
        {
            // FAIL CLOSED (rule 10 / finding MM): the main file may lack recently committed transactions,
            // so copying it would produce a backup of unknown integrity. Abort — no file, loud error.
            var reason = $"wal_checkpoint(TRUNCATE) still busy after {CheckpointAttempts} attempt(s) — no backup written";
            logger.LogError(
                "Local backup ABORTED (fail closed): {Reason}. A reader (e.g. the Api) is holding the WAL; " +
                "re-launch after it finishes, or stop the reader. A backup of unknown integrity is worse than a missing one with a visible error.",
                reason);
            return new LocalBackupResult(false, null, 0, reason);
        }

        File.Copy(dbPath, backupFile, overwrite: false);

        var pruned = PruneOldBackups(backupDir, today, ops.BackupRetentionDays);
        logger.LogInformation("Local backup: wrote {File}; pruned {Pruned} copy(ies) older than {Days} day(s).",
            backupFile, pruned, ops.BackupRetentionDays);
        return new LocalBackupResult(true, backupFile, pruned);
    }

    private async Task<int> CheckpointWithRetryAsync(AlphaLabDbContext db, CancellationToken ct)
    {
        var busy = 0;
        for (var attempt = 1; attempt <= CheckpointAttempts; attempt++)
        {
            busy = Checkpoint(db);
            if (busy == 0) return 0;
            logger.LogWarning("Local backup: wal_checkpoint(TRUNCATE) busy={Busy} (attempt {Attempt}/{Max}) — a reader is using the WAL.",
                busy, attempt, CheckpointAttempts);
            if (attempt < CheckpointAttempts) await Task.Delay(CheckpointRetryDelay, ct).ConfigureAwait(false);
        }
        return busy;
    }

    // Fold the WAL into the main .db so the file copy is a complete, consistent snapshot. Returns the
    // pragma's busy column: non-zero means the checkpoint could NOT complete (a reader holds the WAL) and
    // the main file may lack recently committed transactions — the CALLER must abort rather than copy
    // (rule 10). CommandTimeout bounds the per-attempt wait on SQLite's internal busy handler.
    private int Checkpoint(AlphaLabDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.CommandTimeout = CheckpointBusyWaitSeconds;
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetInt32(0) : 0;
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
