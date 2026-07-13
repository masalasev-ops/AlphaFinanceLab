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
    public void Schema_ExactlyTheFiveInfraTables_Exist()
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

            Assert.Equal(new[] { "catchup_log", "config", "jobs", "runs", "worker_state" }, tables);
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

            // Plain INTEGER PRIMARY KEY per SCHEMA — the migration hand-edit removed AUTOINCREMENT.
            Assert.DoesNotContain("AUTOINCREMENT", TableDdl(dbPath, "runs"), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AUTOINCREMENT", TableDdl(dbPath, "jobs"), StringComparison.OrdinalIgnoreCase);

            // sqlite_sequence exists iff some table uses AUTOINCREMENT — assert it was never created.
            using var db2 = NewContext(dbPath);
            var conn = db2.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='sqlite_sequence';";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
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
