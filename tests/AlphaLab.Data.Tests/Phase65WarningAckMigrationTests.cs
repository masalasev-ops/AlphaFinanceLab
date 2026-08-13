using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlphaLab.Data.Tests;

/// <summary>
/// M12 (Phase65WarningAckJournalKind) on the path that matters: UP onto a store that already carries
/// journal rows.
///
/// **The live sp500 arena holds ZERO journal_entries rows, and that was verified before the design was
/// chosen — but it is not why this test exists.** The migration ships to EVERY arena, and a rebuild has two
/// failure modes an additive migration structurally cannot have: it can lose rows, and it can regenerate
/// DDL that differs from what the rule-14 hand-edit left behind. Both are asserted here rather than trusted
/// to the from-scratch schema tests, which would see a correct final shape either way. Same reasoning, and
/// deliberately the same shape, as <c>Phase5JobKindMigrationTests</c> (M9).
/// </summary>
public class Phase65WarningAckMigrationTests
{
    /// <summary>The migration immediately before M12 — the 6.4 membership-provenance column.</summary>
    private const string BeforeM12 = "20260807132833_Phase64MembershipProvenance";

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
    public void M12_RebuildsJournalEntries_KeepingEveryRow_AndTheHandEditedPrimaryKey()
    {
        var path = Path.Combine(Path.GetTempPath(), "alphalab-m12-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = NewContext(path))
            {
                db.GetService<IMigrator>().Migrate(BeforeM12);

                // A LOCKED pre-registered hypothesis and an UNLOCKED draft. `locked` is the column D157's
                // gate arm reads, so a rebuild that reset it would silently turn every operator's
                // pre-registration into a draft — and, after D157, turn a real acknowledgment into one the
                // gate ignores. The nullable analytic columns are carried too: they are the D110/D116
                // detectability record, and a rebuild that blanked them would lose evidence nothing
                // recomputes.
                Sql(db,
                    "INSERT INTO journal_entries (entry_id, created_on, kind, title, body_md, strategy_id, locked, expected_effect_ann, detectability_floor_ann, prior_prob) " +
                    "  VALUES (1, '2026-08-01', 'hypothesis', 'a locked pre-registration', 'body', 'cand:a', 1, 0.08, 0.0695, 0.3);" +
                    "INSERT INTO journal_entries (entry_id, created_on, kind, title, body_md, locked) " +
                    "  VALUES (2, '2026-08-02', 'skeptic_review', 'an unlocked draft', 'body', 0);");
            }

            using (var db = NewContext(path))
            {
                db.Database.Migrate();   // M12

                Assert.Equal(2, Scalar<long>(db, "SELECT count(*) FROM journal_entries;"));

                // The lock survives in BOTH directions — the gate's whole forgery guard rests on it.
                Assert.Equal(1L, Scalar<long>(db, "SELECT locked FROM journal_entries WHERE entry_id = 1;"));
                Assert.Equal(0L, Scalar<long>(db, "SELECT locked FROM journal_entries WHERE entry_id = 2;"));

                Assert.Equal("cand:a", Scalar<string>(db, "SELECT strategy_id FROM journal_entries WHERE entry_id = 1;"));
                Assert.Equal(0.0695, Scalar<double>(db, "SELECT detectability_floor_ann FROM journal_entries WHERE entry_id = 1;"), 6);

                // Nullable columns survive as NULL, not as empty strings or zeroes.
                Assert.Equal(0L, Scalar<long>(db, "SELECT count(*) FROM journal_entries WHERE entry_id = 2 AND strategy_id IS NOT NULL;"));
                Assert.Equal(0L, Scalar<long>(db, "SELECT count(*) FROM journal_entries WHERE entry_id = 2 AND prior_prob IS NOT NULL;"));

                var ddl = Scalar<string>(db, "SELECT sql FROM sqlite_master WHERE type='table' AND name='journal_entries';");

                // The DDL is hand-written precisely so this holds: EF's generated rebuild re-adds the
                // AUTOINCREMENT that rule 14's InitialCreate hand-edit stripped.
                Assert.DoesNotContain("AUTOINCREMENT", ddl, StringComparison.OrdinalIgnoreCase);

                // …and the new kind is actually admitted, which is the whole point of the rebuild.
                Assert.Contains("warning_ack", ddl, StringComparison.Ordinal);
                Sql(db,
                    "INSERT INTO journal_entries (entry_id, created_on, kind, title, body_md, strategy_id, locked) " +
                    "  VALUES (3, '2026-08-03', 'warning_ack', 'ack', 'S6 elevated_neg_alpha t=-2.4', 'cand:a', 1);");

                // The OTHER CHECK survived the rebuild — a rebuild that drops a constraint it was not asked
                // to touch is the quiet half of this failure mode.
                Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Sql(db,
                    "INSERT INTO journal_entries (entry_id, created_on, kind, title, body_md, outcome) " +
                    "  VALUES (4, '2026-08-03', 'outcome', 't', 'b', 'invented');"));

                // And the kind CHECK still refuses an unknown kind, so widening it did not disable it.
                Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Sql(db,
                    "INSERT INTO journal_entries (entry_id, created_on, kind, title, body_md) " +
                    "  VALUES (5, '2026-08-03', 'not_a_kind', 't', 'b');"));

                // And the temporary table is gone rather than left beside the real one.
                Assert.Equal(0L, Scalar<long>(db,
                    "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='journal_entries_rebuild';"));
            }
        }
        finally
        {
            // P20: process-global; safe ONLY because parallelization is disabled assembly-wide.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }
}
