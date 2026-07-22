using System.Text.RegularExpressions;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The M5 (Phase4Replay) migration and its D-numbers: D93 (regime run_kind quarantine — P6 resolved),
/// D94 (processed_on dropped with a fail-loud precondition — P5 resolved), the D89/FR-40
/// journal_entries.expected_effect_ann column, and the D89/FR-41 replay_regime_outcomes table.
/// Includes the up-from-M4 path on a store carrying rows, not just the from-scratch path.
/// </summary>
public class Phase4ReplayMigrationTests
{
    private const string M4 = "20260722031355_Phase35PositionSnapshots";

    private static AlphaLabDbContext NewContext(string dbPath) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={dbPath}").Options);

    private static string TableDdl(AlphaLabDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$n;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$n";
        p.Value = table;
        cmd.Parameters.Add(p);
        return (string)cmd.ExecuteScalar()!;
    }

    private static void Sql(AlphaLabDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- M5 fidelity on a from-scratch store ----

    [Fact]
    public void M5_Schema_RegimeQuarantine_ExpectedEffect_ReplayOutcomes()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);

            // D93: regime_labels PK is (as_of, run_kind); regime_episodes carries run_kind + the
            // per-kind latest-episode index.
            Assert.Matches(
                new Regex(@"PRIMARY KEY\s*\(\s*""?as_of""?\s*,\s*""?run_kind""?\s*\)", RegexOptions.IgnoreCase),
                TableDdl(db, "regime_labels"));
            Assert.Contains("run_kind", TableDdl(db, "regime_episodes"));

            // D94: processed_on is GONE.
            Assert.DoesNotContain("processed_on", TableDdl(db, "corporate_actions"));

            // D89/FR-40: the fourth pre-declared field exists.
            Assert.Contains("expected_effect_ann", TableDdl(db, "journal_entries"));

            // D89/FR-41: replay_regime_outcomes — composite PK, run_kind DEFAULT 'replay'.
            var ddl = TableDdl(db, "replay_regime_outcomes");
            Assert.Matches(
                new Regex(@"PRIMARY KEY\s*\(\s*""?strategy_id""?\s*,\s*""?regime_episode_id""?\s*,\s*""?run_kind""?\s*\)", RegexOptions.IgnoreCase),
                ddl);
            Assert.Contains("'replay'", ddl);
            Assert.DoesNotContain("AUTOINCREMENT", ddl, StringComparison.OrdinalIgnoreCase);
        }
        finally { TestDb.Delete(path); }
    }

    // ---- M5 up-from-M4: an existing store with rows in every touched table migrates cleanly ----

    [Fact]
    public void M5_UpFromM4_MigratesAStoreWithRows_AndDropsProcessedOn()
    {
        var path = Path.Combine(Path.GetTempPath(), "alphalab-m5-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = NewContext(path))
            {
                db.GetService<IMigrator>().Migrate(M4); // the live arena's pre-Phase-4 state
                // Rows in every M5-touched table, written through raw SQL (the M4-era shapes).
                Sql(db,
                    "INSERT INTO securities (security_id, current_symbol, first_seen) VALUES (1, 'AAPL', '2020-01-01');" +
                    "INSERT INTO corporate_actions (security_id, type, effective_date, version, observed_at, source, processed_on) " +
                    "  VALUES (1, 'dividend', '2026-01-05', 1, '2026-01-05T20:00:00Z', 'eodhd', NULL);" +
                    "INSERT INTO regime_labels (as_of, trend, vol, label, inputs_hash) VALUES ('2026-01-05', 'bull', 'normal_vol', 'bull/normal_vol', 'h1');" +
                    "INSERT INTO regime_episodes (label, start_date) VALUES ('bull', '2026-01-05');" +
                    "INSERT INTO journal_entries (created_on, kind, title, body_md, locked) VALUES ('2026-01-05', 'hypothesis', 't', 'b', 1);");
            }

            using (var db = NewContext(path))
            {
                db.Database.Migrate(); // → M5

                // The rows survived the table rebuilds with the new defaults.
                var label = db.RegimeLabels.Single();
                Assert.Equal("live", label.RunKind);
                Assert.Equal("bull", label.Trend);
                Assert.Equal("live", db.RegimeEpisodes.Single().RunKind);
                Assert.Null(db.JournalEntries.Single().ExpectedEffectAnn);
                Assert.Single(db.CorporateActions.ToList());
                Assert.DoesNotContain("processed_on", TableDdl(db, "corporate_actions"));
            }
        }
        finally { TryDelete(path); }
    }

    // ---- D94 precondition: a store where processed_on was EVER written fails the migration loudly ----

    [Fact]
    public void D94_Precondition_NonNullProcessedOn_FailsTheMigrationLoudly_NothingDropped()
    {
        var path = Path.Combine(Path.GetTempPath(), "alphalab-d94-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = NewContext(path))
            {
                db.GetService<IMigrator>().Migrate(M4);
                // The impossible row: something DID write processed_on. D94's whole premise ("always
                // NULL, provably never written") would be false — the migration must refuse, not shrug.
                Sql(db,
                    "INSERT INTO securities (security_id, current_symbol, first_seen) VALUES (1, 'AAPL', '2020-01-01');" +
                    "INSERT INTO corporate_actions (security_id, type, effective_date, version, observed_at, source, processed_on) " +
                    "  VALUES (1, 'dividend', '2026-01-05', 1, '2026-01-05T20:00:00Z', 'eodhd', '2026-01-06');");
            }

            using (var db = NewContext(path))
            {
                Assert.ThrowsAny<Exception>(() => db.Database.Migrate());
            }

            // Fail CLOSED: the column (and its data) survived the refused migration.
            using (var db = NewContext(path))
            {
                Assert.Contains("processed_on", TableDdl(db, "corporate_actions"));
            }
        }
        finally { TryDelete(path); }
    }

    // ---- D93 in action: a replay label/episode chain never touches the forward one (P6) ----

    [Fact]
    public void FR26_RegimeLabels_ReplayNeverOverwritesForward()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var opts = new RegimeOptions();
            var proxyId = new RegimeProxyIngestion(db).ResolveProxySecurityId(RegimeProxySource.EodhdGspc, Day(0));
            SeedRisingBars(db, proxyId, 960);
            var service = new RegimeLabelService(db, new BarReadService(db), new RegimeProxyReadiness(db, opts), opts);

            // The forward label at D(959), then a REPLAY recompute of the SAME session at a different
            // (earlier-shaped) watermark. Before D93 the replay write would have replaced the forward row.
            var live = service.ComputeAndSave(Day(959), "2013-06-01T00:00:00Z");
            Assert.True(live.Computed);
            var forwardBefore = Snapshot(db, "live");

            var replay = service.ComputeAndSave(Day(959), "2013-03-01T00:00:00Z", "replay");
            Assert.True(replay.Computed);

            // The forward row is byte-identical; the replay row coexists under its own key with its own
            // provenance (the watermark is IN inputs_hash, so the two hashes differ).
            Assert.Equal(forwardBefore, Snapshot(db, "live"));
            var replayRow = Assert.Single(db.RegimeLabels.Where(l => l.RunKind == "replay").ToList());
            Assert.NotEqual(
                db.RegimeLabels.Single(l => l.RunKind == "live" && l.AsOf == Day(959)).InputsHash,
                replayRow.InputsHash);

            // And the episode chains are per-kind: one forward episode, one replay episode, disjoint.
            Assert.Single(db.RegimeEpisodes.Where(e => e.RunKind == "live").ToList());
            Assert.Single(db.RegimeEpisodes.Where(e => e.RunKind == "replay").ToList());
        }
        finally { TestDb.Delete(path); }
    }

    private static readonly DateOnly Start = new(2000, 1, 1);
    private static string Day(int i) => Start.AddDays(i).ToString("yyyy-MM-dd");

    private static void SeedRisingBars(AlphaLabDbContext db, long id, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var c = 100.0 * Math.Pow(1.0002, i);
            db.Bars.Add(new BarRow
            {
                SecurityId = id, Date = Day(i), Version = 1, ObservedAt = "2013-01-01T00:00:00Z",
                Close = c, AdjClose = c, Source = RegimeProxySource.EodhdGspc
            });
        }
        db.SaveChanges();
    }

    private static string Snapshot(AlphaLabDbContext db, string runKind) =>
        string.Join("|", db.RegimeLabels.Where(l => l.RunKind == runKind)
            .OrderBy(l => l.AsOf)
            .Select(l => l.AsOf + l.Trend + l.Vol + l.Label + l.InputsHash));

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); }
            catch { /* best effort */ }
        }
    }
}
