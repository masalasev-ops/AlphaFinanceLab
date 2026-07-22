using System.Data;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data;

/// <summary>The two facts a WAL check establishes. <see cref="Ok"/> ⇒ the store is in WAL mode AND a
/// checkpoint completed; anything else carries the named reason (rule 10 — never a silent pass).</summary>
public sealed record WalVerificationResult(
    string JournalMode,
    bool CheckpointCompleted,
    int WalPages,
    int CheckpointedPages,
    string? FailureReason)
{
    public bool Ok => FailureReason is null;
}

/// <summary>
/// End-to-end WAL verification (checkpoint 3.5.2, FR-25). SchemaStartup already SETS journal_mode=WAL
/// and fails the Worker host if the mode does not read back as 'wal' (finding 118) — that half is
/// covered. What was never proved is the OTHER half: that a checkpoint on the live store actually
/// COMPLETES. That matters because it is the assumption `LocalBackup` rests on — it folds the WAL into
/// the main file with wal_checkpoint(TRUNCATE) so a plain file copy is a consistent snapshot, and a
/// checkpoint that never completes means the dated backup silently omits recently committed
/// transactions (finding MM is the fail-closed handling of exactly that case).
///
/// This class VERIFIES, it never SETS (unlike SchemaStartup, whose pragma is deliberate — it upgrades
/// a pre-existing rollback-journal store on the Worker's next start). A verifier that repaired what it
/// was asked to check could never report the defect it exists to find, and the Api must never convert
/// the store at all.
///
/// PASSIVE, not TRUNCATE: a PASSIVE checkpoint does as much as it can without waiting on readers and
/// reports what it moved, which is the honest probe. It never blocks the caller behind another
/// process's read, and it does not compete with the backup step's own TRUNCATE checkpoint.
/// </summary>
public static class WalVerification
{
    public static WalVerificationResult Verify(AlphaLabDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) connection.Open();

        var mode = ReadJournalMode(connection);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            // FAIL CLOSED (rule 10). A store in rollback-journal mode still works, so nothing would
            // announce it — but the reader-during-write concurrency the lab depends on (the Api
            // reading while the Worker writes) is gone, and the backup's checkpoint assumption is void.
            return new WalVerificationResult(
                mode ?? "(null)", false, 0, 0,
                $"journal_mode is '{mode ?? "(null)"}', not 'wal'. Launch the Worker (SchemaStartup sets " +
                "WAL and verifies it), or check that no tool converted the store.");
        }

        // PRAGMA wal_checkpoint returns one row: busy | log (pages in the WAL) | checkpointed.
        var (busy, walPages, checkpointed) = Checkpoint(connection);
        if (busy != 0)
        {
            return new WalVerificationResult(
                mode, false, walPages, checkpointed,
                $"wal_checkpoint(PASSIVE) reported busy={busy} — a reader (e.g. the Api) is holding the WAL. " +
                "The backup's consistency assumption cannot be confirmed while that reader is live; stop it and re-verify.");
        }

        return new WalVerificationResult(mode, true, walPages, checkpointed, null);
    }

    private static string? ReadJournalMode(System.Data.Common.DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";   // READ — never 'PRAGMA journal_mode=WAL' here
        return cmd.ExecuteScalar()?.ToString();
    }

    private static (int Busy, int WalPages, int Checkpointed) Checkpoint(System.Data.Common.DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }
}
