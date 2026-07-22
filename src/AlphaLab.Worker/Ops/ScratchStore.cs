using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Ops;

/// <summary>
/// A throwaway copy of the arena store, REWOUND to the moment before a chosen session ran — the
/// substrate `reproduce-day` re-runs that session on (checkpoint 3.5.1, FR-25).
///
/// THE LIVE STORE IS NEVER WRITTEN (D59). The copy is taken through SQLite's online-backup API from a
/// connection opened <c>Mode=ReadOnly</c>: no pragma, no checkpoint, no lock the Worker would care
/// about. Every mutation below happens in the COPY.
///
/// WHAT GETS REWOUND, AND WHAT DELIBERATELY DOES NOT.
///
///   * `bars` and `corporate_actions` are NOT touched. They are versioned append-only and read at a
///     watermark (D40/D76), so a correction observed after the target run is a higher version that a
///     pinned read cannot see — the rewind is already done, by the watermark, at read time. Deleting
///     them would be both unnecessary and a hard-rule-3 violation; not touching them keeps the
///     ci.ps1 no-DELETE-FROM-bars guard true by construction rather than by discipline.
///   * Every table a daily run WRITES is rewound to strictly-before the target session.
///   * `positions` is the exception that made D90 necessary: it is current state, not a log, and
///     corporate actions rewrite it in place with no reversible trade row. It is restored wholesale
///     from `position_snapshots` at the PREVIOUS session — the book as that session closed.
///
/// FAIL CLOSED (rule 10). After rewinding, <see cref="AssertRewound"/> re-reads every rewound table
/// and throws if any row at or after the target session survived. This is not belt-and-braces: a
/// missed table would leave the target day's OWN output in place, the re-run would upsert onto it,
/// and the comparison would pass vacuously — the one failure mode that turns a determinism proof into
/// a lie. Better a loud refusal than a green tick that means nothing.
///
/// KNOWN AS-OF GAPS (documented, not silently accepted — PROGRESS proposal P14).
///   1. `config` rows resolve by MAX(version) with no watermark filter, and `strategies.status` is
///      current state, so a change to either after the target session is visible to the re-run.
///      Neither can move the compared set (decisions, fills, equity, population draws): the funnel
///      reads strategy PLANS, not status, and the population engine draws from
///      (familySeed, memberIndex, gridOrdinal) alone. They can change which post-commit EVALUATION
///      path runs, which is why the comparison does not include it.
///   2. An OUT-OF-BAND write to `positions` between two sessions is outside the D90 chain. Nothing
///      does that today — a freeze during a run is recorded by that run's own snapshot — but the
///      Phase-7 D55 admin-intervention panel is exactly such a write. When it lands, either admin
///      writes join the as-of chain or reproduce-day must report an intervening admin action as a
///      KNOWN divergence rather than a failure.
/// </summary>
public sealed class ScratchStore : IDisposable
{
    /// <summary>Tables a daily run writes, with the column that dates each row. Rewound to
    /// strictly-before the target session. Keep in sync with <see cref="Untouched"/> — every DbSet on
    /// the context must appear in exactly one of the two (ScratchStoreClassificationTests).</summary>
    private static readonly string[] RewoundTables =
    [
        "runs", "catchup_log", "data_quality_flags", "regime_labels", "regime_episodes",
        "trades", "cash_events", "equity_curve", "decisions", "capacity_rejections",
        "position_snapshots", "control_equity", "power_reports", "go_live_log",
        "allocation_log", "overfitting_checks", "overfitting_status",
    ];

    /// <summary>Tables carried across unchanged, each for a stated reason: watermark-resolved
    /// (bars, corporate_actions), as-of resolved by their own date columns (index_membership,
    /// ticker_history), static reference data (securities, trading_calendar), registries the run reads
    /// but does not date (strategies, accounts, control_populations, trials_registry), or operator
    /// state outside the run (config, jobs, journal_entries, api_usage_log, sector_changes,
    /// index_membership_log). `positions` and `worker_state` are handled specially.</summary>
    private static readonly string[] Untouched =
    [
        "config", "jobs", "securities", "ticker_history", "sector_changes", "bars",
        "corporate_actions", "index_membership_log", "index_membership", "trading_calendar",
        "api_usage_log", "strategies", "accounts", "control_populations", "trials_registry",
        "journal_entries",
    ];

    /// <summary>Handled by dedicated logic rather than a date filter: `positions` is restored from the
    /// prior session's D90 snapshot, `worker_state` is reset to idle.</summary>
    private static readonly string[] SpeciallyHandled = ["positions", "worker_state"];

    public string DbPath { get; }
    public string ConnectionString { get; }

    private ScratchStore(string dbPath)
    {
        DbPath = dbPath;
        ConnectionString = Scratch(dbPath);
    }

    // Pooling=false so the scratch file's handles close with their contexts and Dispose can delete it.
    // The alternative — SqliteConnection.ClearAllPools() — is process-global and would also drop the
    // LIVE store's idle pooled connections, which is not this class's business.
    private static string Scratch(string dbPath) =>
        new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();

    /// <summary>Every table name the classification covers — the schema-drift guard's input.</summary>
    public static IReadOnlyList<string> ClassifiedTables =>
        [.. RewoundTables, .. Untouched, .. SpeciallyHandled];

    /// <summary>
    /// Copy the live store, rewind it to just before <paramref name="asOf"/>, and hand back the copy.
    /// <paramref name="previousSession"/> is the session whose closing book seeds `positions` (null ⇒
    /// the target session is at or before inception, so the book starts empty).
    /// </summary>
    public static ScratchStore CreateRewound(
        string liveConnectionString, string asOf, string? previousSession, string scratchPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchPath);

        CopyReadOnly(liveConnectionString, scratchPath);
        var scratch = new ScratchStore(scratchPath);

        using (var db = scratch.OpenContext())
        {
            scratch.Rewind(db, asOf, previousSession);
            ScratchStoreGuard.Assert(db, asOf);
        }
        return scratch;
    }

    public AlphaLabDbContext OpenContext() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(ConnectionString).Options);

    // ---- step 1: the read-only copy ----

    private static void CopyReadOnly(string liveConnectionString, string scratchPath)
    {
        var directory = Path.GetDirectoryName(scratchPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(scratchPath)) File.Delete(scratchPath);

        // Mode=ReadOnly is the D59 guarantee in mechanical form: this connection CANNOT write the live
        // arena even if the code below were wrong. BackupDatabase reads a consistent snapshot without
        // checkpointing, so it also never disturbs a Worker or Api holding the WAL.
        var readOnly = new SqliteConnectionStringBuilder(liveConnectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var source = new SqliteConnection(readOnly);
        source.Open();
        using var destination = new SqliteConnection(Scratch(scratchPath));
        destination.Open();
        source.BackupDatabase(destination);
    }

    // ---- step 2: the rewind ----

    private void Rewind(AlphaLabDbContext db, string asOf, string? previousSession)
    {
        // The run rows being removed first, so data_quality_flags can be cut by their run_id (its only
        // link to a date is the run that wrote it).
        var doomedRunIds = db.Runs
            .Where(r => string.Compare(r.AsOf, asOf) >= 0)
            .Select(r => r.RunId)
            .ToList();
        db.DataQualityFlags.Where(f => doomedRunIds.Contains(f.RunId)).ExecuteDelete();
        db.Runs.Where(r => string.Compare(r.AsOf, asOf) >= 0).ExecuteDelete();
        db.CatchupLog.Where(c => string.Compare(c.AsOf, asOf) >= 0).ExecuteDelete();

        db.RegimeLabels.Where(r => string.Compare(r.AsOf, asOf) >= 0).ExecuteDelete();
        // An episode that STARTED at or after asOf never existed yet; one that started earlier and is
        // still open is correct as it stands, but a closed one must re-open (its end date is in the
        // rewound future). EndDate is nulled rather than the row deleted — the episode itself is real.
        db.RegimeEpisodes.Where(e => string.Compare(e.StartDate, asOf) >= 0).ExecuteDelete();
        db.RegimeEpisodes
            .Where(e => e.EndDate != null && string.Compare(e.EndDate, asOf) >= 0)
            .ExecuteUpdate(s => s.SetProperty(e => e.EndDate, (string?)null));

        // A trade is dated twice (decided at close T, filled at open T+1). Either date landing at or
        // after the target session makes it part of the rewound future.
        db.Trades
            .Where(t => string.Compare(t.DecidedOn, asOf) >= 0 || string.Compare(t.FilledOn, asOf) >= 0)
            .ExecuteDelete();
        db.CashEvents.Where(c => string.Compare(c.AsOf, asOf) >= 0).ExecuteDelete();
        db.EquityCurve.Where(e => string.Compare(e.AsOf, asOf) >= 0).ExecuteDelete();
        db.Decisions.Where(d => string.Compare(d.AsOf, asOf) >= 0).ExecuteDelete();
        db.CapacityRejections.Where(c => string.Compare(c.AsOf, asOf) >= 0).ExecuteDelete();
        db.PositionSnapshots.Where(p => string.Compare(p.AsOf, asOf) >= 0).ExecuteDelete();

        db.ControlEquity.Where(c => string.Compare(c.AsOf, asOf) >= 0).ExecuteDelete();
        db.PowerReports.Where(p => string.Compare(p.AsOf, asOf) >= 0).ExecuteDelete();
        db.GoLiveLog.Where(g => string.Compare(g.AsOf, asOf) >= 0).ExecuteDelete();
        db.AllocationLog.Where(a => string.Compare(a.AsOf, asOf) >= 0).ExecuteDelete();
        db.OverfittingChecks.Where(o => string.Compare(o.AsOf, asOf) >= 0).ExecuteDelete();
        db.OverfittingStatus.Where(o => string.Compare(o.AsOf, asOf) >= 0).ExecuteDelete();

        RestoreBook(db, previousSession);
        ResetWorkerState(db);
    }

    // `positions` is current state. Replace it wholesale with the D90 book as the previous session
    // closed — the one thing the trade log cannot reconstruct, because corporate actions rewrite
    // positions in place (split share counts, merger conversions, spin-off lines) without a
    // reversible row. No previous snapshot ⇒ an empty book, which is exactly inception.
    private static void RestoreBook(AlphaLabDbContext db, string? previousSession)
    {
        db.Positions.ExecuteDelete();
        if (previousSession is null) return;

        var book = db.PositionSnapshots
            .Where(p => p.AsOf == previousSession && p.RunKind == "live")
            .AsEnumerable()
            .Select(p => new PositionRow
            {
                AccountId = p.AccountId,
                SecurityId = p.SecurityId,
                Shares = p.Shares,
                CostBasis = p.CostBasis,
                OpenedOn = p.OpenedOn,
                Frozen = p.Frozen,
                FrozenReason = p.FrozenReason,
            })
            .ToList();

        db.Positions.AddRange(book);
        db.SaveChanges();
    }

    // The reproduce run opens its own run row, so the copied liveness state must not look like a
    // crashed writer (that would 409 nothing here, but it would leave the scratch store's worker_state
    // disagreeing with its runs table — an inconsistency a reader of the scratch file would trip on).
    private static void ResetWorkerState(AlphaLabDbContext db)
    {
        var state = db.WorkerState.FirstOrDefault(w => w.Id == 1);
        if (state is null) return;
        state.RunInProgress = 0;
        state.CurrentRunId = null;
        db.SaveChanges();
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(DbPath + suffix)) File.Delete(DbPath + suffix); }
            catch (IOException) { /* best effort — a scratch file left behind is noise, not a fault */ }
        }
    }
}

/// <summary>
/// The fail-closed proof that a <see cref="ScratchStore"/> rewind actually emptied the target session
/// (rule 10). Separated from the rewind itself so the CHECK cannot share a bug with the thing it
/// checks, and so a test can run it against a deliberately re-planted store.
///
/// This is the guard against the only failure mode that would make `reproduce-day` lie: if a rewound
/// table were missed, the target day's own committed output would still be sitting there, the re-run
/// would upsert onto it, and the comparison would report a byte-identical match having proved nothing.
/// A loud refusal is the only acceptable behaviour.
/// </summary>
public static class ScratchStoreGuard
{
    public static void Assert(AlphaLabDbContext db, string asOf)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var survivors = new List<string>();

        void Check(string table, int count)
        {
            if (count > 0) survivors.Add($"{table} ({count} row(s))");
        }

        Check("runs", db.Runs.Count(r => string.Compare(r.AsOf, asOf) >= 0));
        Check("catchup_log", db.CatchupLog.Count(c => string.Compare(c.AsOf, asOf) >= 0));
        Check("regime_labels", db.RegimeLabels.Count(r => string.Compare(r.AsOf, asOf) >= 0));
        Check("regime_episodes", db.RegimeEpisodes.Count(e => string.Compare(e.StartDate, asOf) >= 0));
        Check("trades", db.Trades.Count(t =>
            string.Compare(t.DecidedOn, asOf) >= 0 || string.Compare(t.FilledOn, asOf) >= 0));
        Check("cash_events", db.CashEvents.Count(c => string.Compare(c.AsOf, asOf) >= 0));
        Check("equity_curve", db.EquityCurve.Count(e => string.Compare(e.AsOf, asOf) >= 0));
        Check("decisions", db.Decisions.Count(d => string.Compare(d.AsOf, asOf) >= 0));
        Check("capacity_rejections", db.CapacityRejections.Count(c => string.Compare(c.AsOf, asOf) >= 0));
        Check("position_snapshots", db.PositionSnapshots.Count(p => string.Compare(p.AsOf, asOf) >= 0));
        Check("control_equity", db.ControlEquity.Count(c => string.Compare(c.AsOf, asOf) >= 0));
        Check("power_reports", db.PowerReports.Count(p => string.Compare(p.AsOf, asOf) >= 0));
        Check("go_live_log", db.GoLiveLog.Count(g => string.Compare(g.AsOf, asOf) >= 0));
        Check("allocation_log", db.AllocationLog.Count(a => string.Compare(a.AsOf, asOf) >= 0));
        Check("overfitting_checks", db.OverfittingChecks.Count(o => string.Compare(o.AsOf, asOf) >= 0));
        Check("overfitting_status", db.OverfittingStatus.Count(o => string.Compare(o.AsOf, asOf) >= 0));
        Check("data_quality_flags", db.DataQualityFlags.Count(f =>
            db.Runs.Any(r => r.RunId == f.RunId && string.Compare(r.AsOf, asOf) >= 0)));

        if (survivors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Scratch rewind to {asOf} left rows at or after that session in: {string.Join(", ", survivors)}. " +
                "Refusing to reproduce against a store that already contains the day's own output — the " +
                "comparison would pass vacuously and prove nothing (fail closed, rule 10).");
        }
    }
}
