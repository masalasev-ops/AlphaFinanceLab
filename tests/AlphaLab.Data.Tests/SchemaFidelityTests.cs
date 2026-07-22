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
    public void Schema_ExactlyTheThirtySixTables_Exist()
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

            // Phase 0 infra(5) + Phase 1 data(9) + the D77 pre-Phase-2 data_quality_flags(1)
            // + the Phase-2 checkpoint-2.2 ledger(8) + the checkpoint-2.8 regime tables(2) = 25, plus
            // the Phase-3 "honest arena" tables(9): control_populations, control_equity, trials_registry,
            // power_reports, go_live_log, allocation_log, overfitting_checks, overfitting_status, and the
            // D52 journal_entries (needed by the FR-28 CandidateFactory pre-registration) = 34, plus the
            // Phase-3.5 D90 position_snapshots(1) = 35. D90 records the end-of-day book per account per
            // session because `positions` is current state that corporate actions rewrite in place, so a
            // past day's pre-trade book is otherwise unrecoverable and NFR-1 cannot hold (FR-25). Plus the
            // Phase-4 M5 replay_regime_outcomes(1) = 36 — the D89/FR-41 per-regime replay decomposition,
            // run_kind='replay' by construction under the D37 quarantine.
            //
            // STILL deliberately absent, and each for its own reason — this list is the guard
            // against a table appearing before the phase that earns it:
            //   • features                              — NOT built yet. It has no observed_at/version
            //                       column, so it cannot express the watermark read rule (rule 4);
            //                       persisting a feature at watermark W and re-reading it at W' is a leak
            //                       F-LEAK could not catch. IFeatureView computes over the versioned bar
            //                       reader instead — leak-proof by construction. Phase 6 decides its shape.
            //   • trade_evidence                        — Phase 6 (the D44 trade-level track)
            //   • parameter_scans / feature_baselines   — later monitor signals (S4/S5), not S2/S3/S6
            //   • factor_returns / factor_refresh_log   — Phase 6 (French RF ingestion)
            //   • news_items / analysis_cache / llm_budget_log — Phase 5 (the LLM path)
            //   • admin_actions                         — Phase 7 (the D55 admin commands)
            // The ux_runs_ok_forward partial index lands in checkpoint 2.10 (M3), where Stage 2
            // first writes runs.
            Assert.Equal(new[]
            {
                "accounts", "allocation_log", "api_usage_log", "bars", "capacity_rejections",
                "cash_events", "catchup_log", "config", "control_equity", "control_populations",
                "corporate_actions", "data_quality_flags", "decisions", "equity_curve", "go_live_log",
                "index_membership", "index_membership_log", "jobs", "journal_entries", "overfitting_checks",
                "overfitting_status", "position_snapshots", "positions", "power_reports", "regime_episodes",
                "regime_labels", "replay_regime_outcomes", "runs", "sector_changes", "securities",
                "strategies", "ticker_history", "trades", "trading_calendar", "trials_registry", "worker_state"
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
            // Phase 0: runs, jobs. Phase 1: securities, corporate_actions, index_membership_log. D77:
            // data_quality_flags. Checkpoint 2.8: regime_episodes. Phase 2 ledger: accounts, cash_events,
            // decisions, trades. Phase 3: control_populations, trials_registry, power_reports, go_live_log,
            // allocation_log, overfitting_checks, journal_entries (control_equity + overfitting_status
            // have composite PKs, so no autoincrement question arises there).
            foreach (var table in new[]
            {
                "runs", "jobs", "securities", "corporate_actions", "index_membership_log",
                "data_quality_flags", "regime_episodes", "accounts", "cash_events", "decisions", "trades",
                "control_populations", "trials_registry", "power_reports", "go_live_log", "allocation_log",
                "overfitting_checks", "journal_entries"
            })
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
            // regime_labels carries the two cross-product CHECKs (D50); regime_episodes carries none.
            Assert.Contains("ck_regime_labels_trend", TableDdl(dbPath, "regime_labels"));
            Assert.Contains("ck_regime_labels_vol", TableDdl(dbPath, "regime_labels"));
            Assert.Equal(2, Regex.Matches(TableDdl(dbPath, "regime_labels"), "CHECK", RegexOptions.IgnoreCase).Count);
            Assert.Equal(0, Regex.Matches(TableDdl(dbPath, "regime_episodes"), "CHECK", RegexOptions.IgnoreCase).Count);
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

    [Fact]
    public void UxRunsOkForward_RejectsASecondForwardOk_ButAllowsReplayAndFailedRetries()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            // The partial unique index exists on runs (M3 / checkpoint 2.10).
            using (var db = NewContext(dbPath))
            {
                var conn = db.Database.GetDbConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='ux_runs_ok_forward';";
                var sql = (string?)cmd.ExecuteScalar();
                Assert.NotNull(sql);
                Assert.Contains("as_of", sql);
                Assert.Matches(new Regex(@"WHERE.*status.*=.*'ok'", RegexOptions.IgnoreCase | RegexOptions.Singleline), sql!);
            }

            // One 'ok' live row for a day is fine.
            using (var db = NewContext(dbPath))
            {
                db.Runs.Add(new RunRow { AsOf = "2026-01-05", RunKind = "live", Watermark = "w", StartedAt = "t", Status = "ok" });
                db.SaveChanges();
            }

            // A SECOND 'ok' FORWARD row for the same as_of is rejected by the store (the partial index).
            using (var db = NewContext(dbPath))
            {
                db.Runs.Add(new RunRow { AsOf = "2026-01-05", RunKind = "catchup", Watermark = "w", StartedAt = "t", Status = "ok" });
                Assert.ThrowsAny<Exception>(() => db.SaveChanges());
            }

            // But a FAILED retry (same day) and a REPLAY row over the same date are BOTH allowed — the
            // index is partial exactly so retries and replay are exempt (v1.9.7 finding 109).
            using (var db = NewContext(dbPath))
            {
                db.Runs.Add(new RunRow { AsOf = "2026-01-05", RunKind = "live", Watermark = "w", StartedAt = "t", Status = "failed" });
                db.Runs.Add(new RunRow { AsOf = "2026-01-05", RunKind = "replay", Watermark = "w", StartedAt = "t", Status = "ok" });
                db.Runs.Add(new RunRow { AsOf = "2026-01-05", RunKind = "replay", Watermark = "w2", StartedAt = "t", Status = "ok" });
                db.SaveChanges(); // no throw
                Assert.Equal(2, db.Runs.Count(r => r.AsOf == "2026-01-05" && r.RunKind == "replay"));
            }
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_Phase3CheckConstraints_PresentOnStatusAndJournal_AbsentElsewhere()
    {
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            // SCHEMA declares CHECKs on exactly TWO honest-arena tables. Count the uppercase SQL keyword
            // CASE-SENSITIVELY: "overfitting_checks"/"check_id"/"PK_overfitting_checks" contain the
            // substring "check", so an IgnoreCase count would over-count on that table (its DDL has 0 real
            // CHECK constraints but 3 lowercase "check" substrings).
            Assert.Contains("ck_overfitting_status_status", TableDdl(dbPath, "overfitting_status"));
            Assert.Equal(1, Regex.Matches(TableDdl(dbPath, "overfitting_status"), "CHECK").Count);
            Assert.Contains("ck_journal_entries_kind", TableDdl(dbPath, "journal_entries"));
            Assert.Contains("ck_journal_entries_outcome", TableDdl(dbPath, "journal_entries"));
            Assert.Equal(2, Regex.Matches(TableDdl(dbPath, "journal_entries"), "CHECK").Count);

            // The other seven honest-arena tables carry NO CHECK (SCHEMA fidelity) — notably
            // overfitting_checks.signal is unconstrained (S1..S8 + the descriptive 'turnover_match').
            foreach (var table in new[]
            {
                "control_populations", "control_equity", "trials_registry", "power_reports",
                "go_live_log", "allocation_log", "overfitting_checks"
            })
            {
                Assert.Equal(0, Regex.Matches(TableDdl(dbPath, table), "CHECK").Count);
            }
        }
        finally { TryDelete(dbPath); }
    }

    [Fact]
    public void Schema_OverfittingChecksPath_CoveringIndex_Exists()
    {
        // The FR-35 separation state and FR-39 cohort curve read the persisted S3 percentile path per
        // strategy; ix_overfitting_checks_path(strategy_id, signal, as_of) makes that queryable (D88).
        var dbPath = TempDb();
        try
        {
            using (var db = NewContext(dbPath)) db.Database.Migrate();

            using var db2 = NewContext(dbPath);
            var conn = db2.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='ix_overfitting_checks_path';";
            var sql = (string?)cmd.ExecuteScalar();
            Assert.NotNull(sql);
            Assert.Contains("strategy_id", sql);
            Assert.Contains("signal", sql);
            Assert.Contains("as_of", sql);
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
