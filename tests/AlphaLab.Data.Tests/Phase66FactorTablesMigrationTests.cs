using AlphaLab.Data;
using AlphaLab.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// M13 (checkpoint 6.6, D41): `factor_returns` + `factor_refresh_log`.
///
/// **THIS FIXTURE'S JOB IS TO PIN THE SHAPE AS *SPECIFIED*, INCLUDING WHAT IT LACKS.** SCHEMA_v1.9
/// §157-164 gives `factor_returns` three columns and no `observed_at`/`version`, so it cannot express the
/// rule-4 watermark read. That is **finding 443**, whose trigger is checkpoint 6.13 — the first use that
/// is not diagnostic-only. Asserting the absence here is deliberate: when someone adds the column, THIS
/// TEST GOES RED and points at the finding, instead of the column arriving quietly as an implementation
/// detail. `docs/phase6/README.md` is explicit that changing a specified shape is a decision.
/// </summary>
public class Phase66FactorTablesMigrationTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"alphalab-m13-{Guid.NewGuid():N}.db");

    private static AlphaLabDbContext NewContext(string path) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options);

    private static void TryDelete(string path)
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    private static string TableSql(AlphaLabDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.Add(new SqliteParameter("$n", table));
        return (string?)cmd.ExecuteScalar() ?? "";
    }

    [Fact]
    public void FR5_D41_M13_CreatesBothTables_WithTheSchemaVerbatimShape()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();
            using var db2 = NewContext(dbPath);

            var returns = TableSql(db2, "factor_returns");
            var log = TableSql(db2, "factor_refresh_log");

            Assert.Contains("\"date\"", returns, StringComparison.Ordinal);
            Assert.Contains("\"factor\"", returns, StringComparison.Ordinal);
            Assert.Contains("\"value\"", returns, StringComparison.Ordinal);
            Assert.Contains("PRIMARY KEY (\"date\", \"factor\")", returns, StringComparison.Ordinal);

            Assert.Contains("\"refreshed_at\"", log, StringComparison.Ordinal);
            Assert.Contains("\"files_json\"", log, StringComparison.Ordinal);
            Assert.Contains("\"checksum\"", log, StringComparison.Ordinal);
            Assert.Contains("\"rows_added\"", log, StringComparison.Ordinal);
        }
        finally { TryDelete(dbPath); }
    }

    /// <summary>Rule 14: EF must not have emitted AUTOINCREMENT. Both PKs are TEXT or composite-TEXT, so
    /// it does not — verified against the emitted DDL rather than assumed.</summary>
    [Fact]
    public void FR5_D41_M13_NeitherTableUsesAutoincrement()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();
            using var db2 = NewContext(dbPath);

            Assert.DoesNotContain("AUTOINCREMENT", TableSql(db2, "factor_returns"), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AUTOINCREMENT", TableSql(db2, "factor_refresh_log"), StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dbPath); }
    }

    /// <summary>
    /// The ABSENCE that finding 443 is about, asserted so it cannot be closed silently. `factor_returns`
    /// has no availability column, so nothing in it can answer "was this publishable as of X" — which D83
    /// requires the moment residual momentum reads the series as a signal, and which OVERFITTING_MONITOR
    /// §4 requires for S5/S8. Built as specified; the gap is filed, not patched.
    /// </summary>
    [Fact]
    public void FR5_D41_M13_FactorReturnsHasNoAvailabilityColumn_finding443()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();
            using var db2 = NewContext(dbPath);
            var sql = TableSql(db2, "factor_returns");

            Assert.DoesNotContain("observed_at", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("version", sql, StringComparison.Ordinal);
        }
        finally { TryDelete(dbPath); }
    }

    /// <summary>The composite PK is the idempotence mechanism: a re-fetch of an overlapping window cannot
    /// duplicate a (date, factor). Pinned because the refresh in commit 4 depends on it.</summary>
    [Fact]
    public void FR5_D41_M13_TheCompositePk_RefusesADuplicateDateFactorPair()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            using (var db2 = NewContext(dbPath))
            {
                db2.FactorReturns.Add(new FactorReturnRow { Date = "2026-08-03", Factor = "RF", Value = 0.00017 });
                db2.SaveChanges();
            }

            // A SEPARATE context on purpose: reusing the first one would hit EF's identity map and throw
            // before a statement was ever issued, which tests the change tracker rather than the table.
            // The refresh in commit 4 re-fetches overlapping windows across process runs, so the DB
            // constraint is the mechanism that actually has to hold.
            using (var db3 = NewContext(dbPath))
            {
                db3.FactorReturns.Add(new FactorReturnRow { Date = "2026-08-03", Factor = "RF", Value = 0.00019 });
                Assert.Throws<DbUpdateException>(() => db3.SaveChanges());
            }
        }
        finally { TryDelete(dbPath); }
    }
}
