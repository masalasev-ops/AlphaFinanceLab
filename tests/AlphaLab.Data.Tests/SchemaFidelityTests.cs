using System.Text.RegularExpressions;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Asserts the on-disk schema matches SCHEMA_v1.9 on the fidelity points the Phase-0 prompt calls
/// out: config's composite (key, version) PK (finding 108) and runs.status being unconstrained.
/// </summary>
public class SchemaFidelityTests
{
    private static AlphaLabDbContext NewContext(string dbPath) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={dbPath}").Options);

    private static string TableDdl(string dbPath, string table)
    {
        using var db = NewContext(dbPath);
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

    [Fact]
    public void Config_CompositePrimaryKey_And_TwoVersionsPersist_And_DuplicateRejectedByStore()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            var ddl = TableDdl(dbPath, "config");
            Assert.Matches(new Regex(@"PRIMARY KEY\s*\(\s*""?key""?\s*,\s*""?version""?\s*\)", RegexOptions.IgnoreCase), ddl);

            // (k,1) then (k,2) both persist.
            using (var db = NewContext(dbPath))
            {
                db.Config.Add(new ConfigRow { Key = "x", Version = 1, ValueJson = "1", ChangedOn = "t" });
                db.Config.Add(new ConfigRow { Key = "x", Version = 2, ValueJson = "2", ChangedOn = "t" });
                db.SaveChanges();
            }
            using (var db = NewContext(dbPath))
            {
                Assert.Equal(2, db.Config.Count(c => c.Key == "x"));
            }

            // Duplicate (key, version) rejected by the STORE — fresh context so the DB, not the
            // change tracker, does the rejecting.
            using (var db = NewContext(dbPath))
            {
                db.Config.Add(new ConfigRow { Key = "x", Version = 2, ValueJson = "dup", ChangedOn = "t" });
                Assert.ThrowsAny<Exception>(() => db.SaveChanges());
            }
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Runs_Status_IsUnconstrained_OnlyRunKindHasACheck()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            var ddl = TableDdl(dbPath, "runs");
            Assert.Contains("run_kind", ddl);
            // Exactly one CHECK on runs (run_kind). If status carried a CHECK there would be two.
            Assert.Equal(1, Regex.Matches(ddl, "CHECK", RegexOptions.IgnoreCase).Count);
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void WorkerState_IsSeededWithASingleRowIdOne()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            using var db2 = NewContext(dbPath);
            var rows = db2.WorkerState.ToList();
            Assert.Single(rows);
            Assert.Equal(1, rows[0].Id);
            Assert.Equal(0, rows[0].RunInProgress);
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_ExactlyTheFourteenTables_Exist()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            using var db2 = NewContext(dbPath);
            var conn = db2.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '\_\_%' ESCAPE '\' ORDER BY name;";
            var tables = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));

            // Phase 0 infra(5) + Phase 1 data(9). regime_labels/regime_episodes/features/factor_* and
            // the ux_runs_ok_forward partial index are deliberately deferred to Phase 2.
            Assert.Equal(new[]
            {
                "api_usage_log", "bars", "catchup_log", "config", "corporate_actions",
                "index_membership", "index_membership_log", "jobs", "runs", "sector_changes",
                "securities", "ticker_history", "trading_calendar", "worker_state"
            }, tables);
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_TheFourCheckConstraints_ArePresent_AndNoneOnConfigOrCatchup()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            Assert.Contains("ck_runs_run_kind", TableDdl(dbPath, "runs"));
            Assert.Contains("ck_jobs_kind", TableDdl(dbPath, "jobs"));
            Assert.Contains("ck_jobs_status", TableDdl(dbPath, "jobs"));
            Assert.Matches(new Regex(@"CHECK\s*\(\s*id\s*=\s*1\s*\)", RegexOptions.IgnoreCase), TableDdl(dbPath, "worker_state"));
            Assert.Equal(0, Regex.Matches(TableDdl(dbPath, "config"), "CHECK", RegexOptions.IgnoreCase).Count);
            Assert.Equal(0, Regex.Matches(TableDdl(dbPath, "catchup_log"), "CHECK", RegexOptions.IgnoreCase).Count);
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_Defaults_ArePresent()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            Assert.Contains("'running'", TableDdl(dbPath, "runs"));  // runs.status DEFAULT 'running'
            Assert.Contains("'queued'", TableDdl(dbPath, "jobs"));   // jobs.status DEFAULT 'queued'
            Assert.Matches(
                new Regex(@"run_in_progress.*DEFAULT\s*0", RegexOptions.IgnoreCase | RegexOptions.Singleline),
                TableDdl(dbPath, "worker_state"));
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_IntegerPrimaryKey_StillAutoAssignsOnInsert()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            using (var db = NewContext(dbPath))
            {
                db.Runs.Add(new RunRow { AsOf = "2026-01-01", RunKind = "live", Watermark = "w", StartedAt = "t" });
                db.SaveChanges();
            }
            using (var db = NewContext(dbPath))
            {
                Assert.True(db.Runs.Single().RunId > 0); // rowid auto-assigned even without AUTOINCREMENT
            }
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_IntegerPrimaryKeys_HaveNoAutoincrement()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            // Every bare INTEGER PRIMARY KEY per SCHEMA — the migration hand-edit removed AUTOINCREMENT.
            // Phase 0: runs, jobs. Phase 1: securities, corporate_actions, index_membership_log.
            foreach (var table in new[] { "runs", "jobs", "securities", "corporate_actions", "index_membership_log" })
            {
                Assert.DoesNotContain("AUTOINCREMENT", TableDdl(dbPath, table), StringComparison.OrdinalIgnoreCase);
            }

            // sqlite_sequence exists iff SOME table uses AUTOINCREMENT — belt-and-suspenders: asserting it
            // was never created fails automatically if any table (missed or new) slips one through.
            using var db2 = NewContext(dbPath);
            var conn = db2.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='sqlite_sequence';";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_Phase1IntegerPrimaryKeys_StillAutoAssignOnInsert()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            long securityId;
            using (var db = NewContext(dbPath))
            {
                var sec = new SecurityRow { CurrentSymbol = "ACME", Exchange = "US", FirstSeen = "2020-01-01" };
                db.Securities.Add(sec);
                db.SaveChanges();
                securityId = sec.SecurityId;
                Assert.True(securityId > 0); // rowid auto-assigned even without AUTOINCREMENT
            }

            using (var db = NewContext(dbPath))
            {
                var ca = new CorporateActionRow
                {
                    SecurityId = securityId, Type = "dividend", EffectiveDate = "2020-02-01",
                    ObservedAt = "2020-02-01T00:00:00Z", Source = "eodhd"
                };
                db.CorporateActions.Add(ca);
                db.SaveChanges();
                Assert.True(ca.ActionId > 0);
            }
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_Phase1CheckConstraints_ArePresent_AndAbsentWhereSchemaHasNone()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            Assert.Contains("ck_corporate_actions_type", TableDdl(dbPath, "corporate_actions"));
            Assert.Contains("ck_trading_calendar_session", TableDdl(dbPath, "trading_calendar"));
            // These data tables carry no CHECK in SCHEMA.
            Assert.Equal(0, Regex.Matches(TableDdl(dbPath, "securities"), "CHECK", RegexOptions.IgnoreCase).Count);
            Assert.Equal(0, Regex.Matches(TableDdl(dbPath, "bars"), "CHECK", RegexOptions.IgnoreCase).Count);
            Assert.Equal(0, Regex.Matches(TableDdl(dbPath, "index_membership"), "CHECK", RegexOptions.IgnoreCase).Count);
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_CorporateActionsType_CheckRejectsUnknownType()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            using var db2 = NewContext(dbPath);
            db2.Securities.Add(new SecurityRow { CurrentSymbol = "ACME", Exchange = "US", FirstSeen = "2020-01-01" });
            db2.SaveChanges();
            var secId = db2.Securities.Single().SecurityId;

            using var db3 = NewContext(dbPath);
            db3.CorporateActions.Add(new CorporateActionRow
            {
                SecurityId = secId, Type = "not_a_real_type", EffectiveDate = "2020-02-01",
                ObservedAt = "2020-02-01T00:00:00Z", Source = "eodhd"
            });
            Assert.ThrowsAny<Exception>(() => db3.SaveChanges());
        }
        finally { TryDelete(dbPath); }
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), "alphalab-schema-" + Guid.NewGuid().ToString("N") + ".db");

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); }
            catch { /* best effort */ }
        }
    }
}
