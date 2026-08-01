using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlphaLab.Data.Tests;

/// <summary>
/// M9 (Phase5HypothesesJobKind) on the path that matters: UP onto a store that already carries job rows.
///
/// M9 is the first migration in the corpus that REBUILDS a table rather than adding to one, because SQLite
/// cannot ALTER a CHECK constraint. A rebuild has two failure modes an additive migration structurally
/// cannot have — it can lose rows, and it can regenerate DDL that differs from what the hand-edit left
/// behind — so both are asserted here rather than trusted to the from-scratch schema tests, which would
/// see a correct final shape either way.
/// </summary>
public class Phase5JobKindMigrationTests
{
    /// <summary>The migration immediately before M9 — the AI-seat tables (checkpoint 5.5).</summary>
    private const string BeforeM9 = "20260801163418_Phase5AiSeatTables";

    private static AlphaLabDbContext NewContext(string dbPath) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={dbPath}").Options);

    private static void Sql(AlphaLabDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(AlphaLabDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void M9_RebuildsJobs_KeepingEveryRow_AndTheHandEditedPrimaryKey()
    {
        var path = Path.Combine(Path.GetTempPath(), "alphalab-m9-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = NewContext(path))
            {
                db.GetService<IMigrator>().Migrate(BeforeM9);

                // A queued job and a finished one. A queued job has nothing to do with the CHECK being
                // widened, and losing it would mean a command the API already accepted (202) silently
                // never runs — the failure mode a rebuild introduces and an ALTER never could.
                Sql(db,
                    "INSERT INTO jobs (job_id, kind, status, submitted_at, request_json) " +
                    "  VALUES (1, 'replay', 'queued', '2026-07-31T10:00:00Z', '{\"a\":1}');" +
                    "INSERT INTO jobs (job_id, kind, status, submitted_at, started_at, finished_at, request_json, error_json) " +
                    "  VALUES (2, 'analysis_brief', 'failed', '2026-07-31T11:00:00Z', '2026-07-31T11:00:01Z', " +
                    "          '2026-07-31T11:00:02Z', '{}', '{\"error\":\"boom\"}');");
            }

            using (var db = NewContext(path))
            {
                db.Database.Migrate();   // M9

                Assert.Equal(2, Scalar<long>(db, "SELECT count(*) FROM jobs;"));
                Assert.Equal("queued", Scalar<string>(db, "SELECT status FROM jobs WHERE job_id = 1;"));
                Assert.Equal("{\"a\":1}", Scalar<string>(db, "SELECT request_json FROM jobs WHERE job_id = 1;"));
                // Nullable columns survive as themselves, not as empty strings.
                Assert.Equal("{\"error\":\"boom\"}", Scalar<string>(db, "SELECT error_json FROM jobs WHERE job_id = 2;"));
                Assert.Equal(0L, Scalar<long>(db, "SELECT count(*) FROM jobs WHERE job_id = 1 AND started_at IS NOT NULL;"));

                var ddl = Scalar<string>(db, "SELECT sql FROM sqlite_master WHERE type='table' AND name='jobs';");

                // The rebuild wrote the DDL BY HAND precisely so this holds: EF's generated rebuild
                // re-adds the AUTOINCREMENT that rule 14's InitialCreate hand-edit stripped.
                Assert.DoesNotContain("AUTOINCREMENT", ddl, StringComparison.OrdinalIgnoreCase);

                // …and the new kind is actually admitted, which is the whole point of the rebuild.
                Assert.Contains("analysis_hypotheses", ddl, StringComparison.Ordinal);
                Sql(db,
                    "INSERT INTO jobs (job_id, kind, status, submitted_at, request_json) " +
                    "  VALUES (3, 'analysis_hypotheses', 'queued', '2026-08-01T10:00:00Z', '{}');");

                // The other CHECK survived the rebuild — a rebuild that drops a constraint it was not
                // asked to touch is the quiet half of this failure mode.
                Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Sql(db,
                    "INSERT INTO jobs (job_id, kind, status, submitted_at, request_json) " +
                    "  VALUES (4, 'replay', 'invented', '2026-08-01T10:00:00Z', '{}');"));

                // And the temporary table is gone rather than left beside the real one.
                Assert.Equal(0L, Scalar<long>(db,
                    "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='jobs_rebuild';"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }
}
