using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlphaLab.Data.Tests;

/// <summary>
/// M11 (Phase64MembershipProvenance) — `observed_at` + `source` on `index_membership_log` (checkpoint
/// 6.4). Both nullable, neither carrying a CHECK, so this is ALTER ADD COLUMN and NOT the table rebuild
/// M9 had to be. That distinction is asserted here rather than assumed, because a rebuild would re-add
/// AUTOINCREMENT (finding 324) to a table SCHEMA declares with a bare INTEGER PRIMARY KEY.
///
/// **THE LOAD-BEARING ASSERTION IS THAT NOTHING IS BACKFILLED.** The only value available to backfill
/// `observed_at` with is `as_of` — the SESSION the reconcile ran for, i.e. the run date — and telling
/// the two apart is the entire reason finding 197 asked for a fetch date. Writing `as_of` into
/// `observed_at` would manufacture provenance that looks authoritative and is false, on rows nobody
/// would ever re-examine. NULL means UNKNOWN, and the read-model is required to render unknown as
/// unknown rather than as stale.
/// </summary>
public class Phase64MembershipProvenanceMigrationTests
{
    /// <summary>The migration immediately before M11 — the Phase-5 proposal inputs.</summary>
    private const string BeforeM11 = "20260801171021_Phase5ProposalInputs";

    // Pooling=False rather than the ClearAllPools() the sibling migration tests use. That idiom is
    // PROCESS-GLOBAL and races xUnit's class parallelism (proposal P20 / finding 387, measured at 1
    // failure in 3 on clean main); adding two more call sites to a known-racy pattern would deepen the
    // problem P20 exists to decide. Disabling the pool for these two fixtures releases the file on
    // dispose, needs no global call, and is P20's own remedy (a) applied where it costs nothing.
    private static AlphaLabDbContext NewContext(string dbPath) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False").Options);

    private static void Sql(AlphaLabDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Ddl(AlphaLabDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='index_membership_log';";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void M11_AddsProvenanceColumns_WithoutBackfillingAnyPreMigrationRow()
    {
        var path = Path.Combine(Path.GetTempPath(), "alphalab-m11-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = NewContext(path))
            {
                db.GetService<IMigrator>().Migrate(BeforeM11);

                // The live arena's two rows, both agreed=1 — the 2026-07-15 bootstrap and the 2026-07-19
                // re-verify. Neither has a recoverable fetch instant, which is the point.
                Sql(db,
                    "INSERT INTO index_membership_log (log_id, as_of, source_count, crosscheck_count, agreed, adds_json, drops_json, note) " +
                    "  VALUES (1, '2026-07-15', 101, 101, 1, '[2,3,4]', '[]', NULL);" +
                    "INSERT INTO index_membership_log (log_id, as_of, source_count, crosscheck_count, agreed, adds_json, drops_json, note) " +
                    "  VALUES (2, '2026-07-19', 101, 101, 1, '[]', '[]', NULL);");
            }

            using (var db = NewContext(path))
            {
                db.Database.Migrate();

                var rows = db.IndexMembershipLog.OrderBy(r => r.LogId).ToList();

                // Row survival: an additive migration must not lose anything.
                Assert.Equal(2, rows.Count);
                Assert.Equal("2026-07-15", rows[0].AsOf);
                Assert.Equal("2026-07-19", rows[1].AsOf);
                Assert.Equal(101, rows[0].SourceCount);
                Assert.Equal("[2,3,4]", rows[0].AddsJson);

                // ...and NOTHING is invented. Not as_of copied across, not a migration timestamp, not
                // the source the operator happens to have used. Unknown stays unknown.
                Assert.All(rows, r => Assert.Null(r.ObservedAt));
                Assert.All(rows, r => Assert.Null(r.Source));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void M11_IsAnAdditiveAlter_NotARebuild_SoNoAutoincrementIsReintroduced()
    {
        var path = Path.Combine(Path.GetTempPath(), "alphalab-m11ddl-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var db = NewContext(path);
            db.Database.Migrate();
            var ddl = Ddl(db);

            Assert.Contains("observed_at", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source", ddl, StringComparison.OrdinalIgnoreCase);

            // finding 324: a rebuild is what re-adds AUTOINCREMENT to a table SCHEMA declares with a
            // bare INTEGER PRIMARY KEY. An ALTER cannot, and this pins that it stayed an ALTER.
            Assert.DoesNotContain("AUTOINCREMENT", ddl, StringComparison.OrdinalIgnoreCase);

            // NO CHECK CONSTRAINT on `source`, deliberately: constrained by convention and by a test
            // instead, so registering a third primary later is an INSERT rather than another rebuild.
            // Finding 324's lesson applied forward rather than after the fact. Matched on the
            // constraint SYNTAX, not the bare word — `crosscheck_count` is a column name and contains it.
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(
                @"\bCHECK\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase), ddl);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
