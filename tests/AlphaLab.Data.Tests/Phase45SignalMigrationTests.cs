using AlphaLab.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The M6 (Phase45SignalLibrary) migration on the path that actually matters: UP FROM M5 on a store
/// that already carries rows, not only the from-scratch path every other schema test exercises.
///
/// M6 is purely additive (two CREATE TABLEs, no ALTER, no table rebuild), so the SQLite rebuild trap
/// that motivated the M5 test — a rebuild silently re-adding the AUTOINCREMENT that rule 14's hand-edit
/// stripped — does not apply here. That is asserted rather than assumed, because "it should be additive"
/// is exactly the belief a future column addition would quietly falsify.
/// </summary>
public class Phase45SignalMigrationTests
{
    private const string M5 = "20260722181746_Phase4Replay";

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

    [Fact]
    public void M6_UpFromM5_OnAStoreWithRows_AddsTheTwoTables_AndDisturbsNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), "alphalab-m6-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = NewContext(path))
            {
                db.GetService<IMigrator>().Migrate(M5);   // the live arena's post-Phase-4 state
                Sql(db,
                    "INSERT INTO securities (security_id, current_symbol, first_seen) VALUES (1, 'AAPL', '2020-01-01');" +
                    "INSERT INTO runs (as_of, run_kind, watermark, started_at, status) " +
                    "  VALUES ('2026-01-05', 'live', '2026-01-05T22:00:00Z', '2026-01-05T22:00:01Z', 'ok');" +
                    "INSERT INTO config (key, value_json, version, changed_on, reason) " +
                    "  VALUES ('Monitor.S6.AutoRetireEvals', '4', 1, '2026-01-05T22:00:00Z', 'freeze');");
            }

            using (var db = NewContext(path))
            {
                Assert.Contains("20260731150019_Phase45SignalLibrary", db.Database.GetPendingMigrations());
                db.Database.Migrate();   // → M6
                Assert.Empty(db.Database.GetPendingMigrations());

                // The new tables exist and accept rows.
                new AlphaLab.Data.Services.SignalRegistrar(db).RegisterV1("2026-01-06");
                db.SignalIc.Add(new SignalIcRow
                { SignalId = "mom:L126", AsOf = "2026-01-05", HorizonDays = 63, RankIc = -0.02, N = 87 });
                db.SaveChanges();
                Assert.Equal(7, db.Signals.Count());
                Assert.Equal(-0.02, db.SignalIc.Single().RankIc, 9);

                // The pre-existing rows are untouched — an additive migration must not rewrite history.
                Assert.Single(db.Securities.ToList());
                Assert.Equal("ok", db.Runs.Single().Status);
                Assert.Equal("4", db.Config.Single(c => c.Key == "Monitor.S6.AutoRetireEvals").ValueJson);

                // And no rebuild smuggled an AUTOINCREMENT back in anywhere (rule 14's closure guard).
                var conn = db.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open) conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='sqlite_sequence';";
                Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
