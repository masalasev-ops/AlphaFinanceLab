using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Worker.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// A migrated on-disk SQLite arena + the D53 <see cref="DailyPipeline"/> built through the REAL DI graph
/// (AddAlphaLabData + AddAlphaLabMembership), with the two live providers swapped for in-memory fakes and
/// a fixed clock. Resolving the pipeline from the same registrations Program.cs uses means the tests also
/// exercise the wiring, not just the pipeline body.
///
/// STANDARD SCENARIO: a 50-session NYSE-shaped calendar, two index members + a cap-weight ETF proxy + the
/// regime proxy, the two config rows (Regime.ProxySecurityId, Benchmark.CapWeightProxySecurityId), and a
/// gently-ramping bar series pre-seeded through the session BEFORE the first run day (so the ADV/vol
/// windows are complete for the T+1 fills). Run days are the three sessions after the pre-seed boundary;
/// the fakes carry those bars so the pipeline fetches + ingests them fresh (LastStoredDate = the pre-seed
/// boundary, so a run-day flag is genuinely new).
/// </summary>
public sealed class PipelineHarness : IDisposable
{
    public const long MemberA = 1, MemberB = 2, CwProxy = 3, RegimeProxy = 4;
    public const string MemberASymbol = "MEMBERA", MemberBSymbol = "MEMBERB", CwSymbol = "OEF", RegimeSymbol = "GSPC";

    private readonly ServiceProvider _provider;
    private readonly string _arenaDir;
    public string DbPath { get; }
    public FakeMarketData Market { get; } = new();
    public FakeRegimeProxy Proxy { get; } = new();
    public IReadOnlyList<string> Sessions { get; }

    /// <summary>The Worker + Ops options the harness registers as singletons — mutable, so a test can tune a
    /// knob (e.g. StaleRunThresholdSeconds, BackupRetentionDays) before running a step.</summary>
    public WorkerOptions WorkerOptions { get; } = new();
    public OpsOptions OpsOptions { get; } = new();

    /// <summary>The fixed wall clock the harness runs at (for computing fresh/stale heartbeat timestamps).</summary>
    public DateTimeOffset Now { get; }

    /// <summary>The store's sibling backups\ directory (D72 / LocalBackup).</summary>
    public string BackupDir => Path.Combine(_arenaDir, "backups");

    /// <summary>The three run days (sessions just past the pre-seed boundary).</summary>
    public string Run1 => Sessions[PreSeedCount];
    public string Run2 => Sessions[PreSeedCount + 1];
    public string Run3 => Sessions[PreSeedCount + 2];

    private const int PreSeedCount = 40; // sessions of history pre-seeded (≥ the 21-session ADV window)

    /// <param name="now">The wall clock for the catch-up guard + run timestamps. Default: Run3 (the last
    /// run day) at 22:00 UTC — past its ET close, so the catch-up "last completed session" is Run3 and the
    /// natural gap is Run1..Run3. The pipeline tests are unaffected (RunDayAsync stamps timestamps from it
    /// but never reads it for the watermark).</param>
    public PipelineHarness(DateTimeOffset? now = null, Action<IServiceCollection>? configure = null)
    {
        // A per-instance directory so the store's sibling backups\ folder (D72 / LocalBackup) is isolated:
        // every harness gets its own <temp>\alphalab-pipe-{guid}\alphalab.db, mirroring <DbBase>\{arena}\.
        _arenaDir = Path.Combine(Path.GetTempPath(), "alphalab-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_arenaDir);
        DbPath = Path.Combine(_arenaDir, "alphalab.db");
        var cs = $"Data Source={DbPath}";

        Sessions = BuildSessions(new DateOnly(2024, 1, 1), 50);
        var clockNow = now ?? new DateTimeOffset(
            DateOnly.ParseExact(Sessions[PreSeedCount + 2], "yyyy-MM-dd", CultureInfo.InvariantCulture)
                .ToDateTime(new TimeOnly(22, 0)), TimeSpan.Zero);
        Now = clockNow;

        using (var db = Open())
        {
            db.Database.Migrate();
            SeedReferenceData(db);
        }
        EnableWal(cs);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" });
        services.AddSingleton(WorkerOptions);
        services.AddSingleton(OpsOptions);
        services.AddAlphaLabData(cs, "sp500", ensureDirectory: true);
        services.AddSingleton(new CostsOptions());
        // Phase 3 (checkpoint 3.3): populations compute in Stage 2. Small M here keeps the shared harness
        // fast — the dedicated determinism/band/perf tests exercise the full M=200.
        services.AddSingleton(new PopulationsOptions { Size = 6, CostFreeSize = 3 });
        services.AddSingleton(new DataQualityOptions());
        services.AddSingleton(new CalendarOptions());
        services.AddSingleton(new CorporateActionsOptions());
        services.AddAlphaLabMembership(new RegimeOptions());
        services.AddSingleton<IMarketDataProvider>(Market);
        services.AddSingleton<IRegimeProxyProvider>(Proxy);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(clockNow));
        services.AddScoped<Stage1Fetch>();
        services.AddScoped<DailyPipeline>();
        services.AddScoped<IMissedSessionResolver, MissedSessionResolver>();
        services.AddSingleton<CatchupRunner>();

        // D72 launch order + liveness (checkpoint 2.12).
        services.AddScoped<HeartbeatWriter>();
        services.AddScoped<IWorkerLiveness, WorkerLivenessReader>();
        services.AddSingleton<StaleRunRecovery>();
        services.AddSingleton<JobDrainer>();
        services.AddSingleton<LocalBackup>();

        configure?.Invoke(services); // e.g. a test-only IJobExecutor for FX-JobDrain
        _provider = services.BuildServiceProvider();
    }

    // ---- D72 launch-order accessors (checkpoint 2.12) ----

    public Task<StaleRunRecoveryResult> RunStaleRecoveryAsync() =>
        _provider.GetRequiredService<StaleRunRecovery>().RecoverAsync();

    public Task<JobDrainOutcome> RunJobDrainAsync() =>
        _provider.GetRequiredService<JobDrainer>().DrainAsync();

    public Task<LocalBackupResult> RunBackupAsync() =>
        _provider.GetRequiredService<LocalBackup>().BackupAsync();

    public bool BeatHeartbeat()
    {
        using var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<HeartbeatWriter>().Beat();
    }

    public async Task<WorkerLiveness> GetLivenessAsync(int thresholdSeconds)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkerLiveness>().GetAsync(thresholdSeconds);
    }

    /// <summary>The whole OnDemand launch order minus StopApplication — the exact sequence OnDemandRunner
    /// drives, run with ONLY Worker+Data components (no Api present): FR34_LabRunsWithoutApi.</summary>
    public async Task<(StaleRunRecoveryResult Recovery, CatchupOutcome Catchup, JobDrainOutcome? Drain, LocalBackupResult Backup)> RunLaunchAsync()
    {
        var recovery = await _provider.GetRequiredService<StaleRunRecovery>().RecoverAsync();
        var catchup = await _provider.GetRequiredService<CatchupRunner>().RunAsync();
        JobDrainOutcome? drain = WorkerOptions.DrainQueuedJobsOnLaunch
            ? await _provider.GetRequiredService<JobDrainer>().DrainAsync()
            : null;
        var backup = await _provider.GetRequiredService<LocalBackup>().BackupAsync();
        return (recovery, catchup, drain, backup);
    }

    /// <summary>Resolve the missed sessions the D47 resolver would report right now.</summary>
    public async Task<IReadOnlyList<DateOnly>> ResolveMissedAsync()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMissedSessionResolver>().ResolveAsync();
    }

    /// <summary>Run the full D47 catch-up loop (resolve → replay each missed day via the pipeline).</summary>
    public Task<CatchupOutcome> RunCatchupAsync() =>
        _provider.GetRequiredService<CatchupRunner>().RunAsync();

    public AlphaLabDbContext Open() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={DbPath}").Options);

    /// <summary>Run one day through a fresh scope (its DbContext is disposed after), so assertion contexts
    /// read committed rows without contending with the pipeline's connection.</summary>
    public async Task<DailyRunResult> RunAsync(string asOf, string runKind = "live")
    {
        using var scope = _provider.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<DailyPipeline>();
        return await pipeline.RunDayAsync(asOf, runKind);
    }

    // ---- scenario seeding ----

    private void SeedReferenceData(AlphaLabDbContext db)
    {
        foreach (var s in Sessions)
        {
            db.TradingCalendar.Add(new TradingCalendarRow { Date = s, Session = "full", CloseTimeLocal = "16:00" });
        }

        Register(db, MemberA, MemberASymbol);
        Register(db, MemberB, MemberBSymbol);
        Register(db, CwProxy, CwSymbol);
        Register(db, RegimeProxy, RegimeSymbol);

        db.IndexMembership.Add(new IndexMembershipRow { SecurityId = MemberA, AddedOn = Sessions[0] });
        db.IndexMembership.Add(new IndexMembershipRow { SecurityId = MemberB, AddedOn = Sessions[0] });

        db.Config.Add(Config(RegimeProxyIngestion.ProxyConfigKey, RegimeProxy.ToString(CultureInfo.InvariantCulture)));
        db.Config.Add(Config(AlphaLab.Strategies.CapWeightProxy.ProxySecurityIdConfigKey, CwProxy.ToString(CultureInfo.InvariantCulture)));

        // Pre-seed the ramping history (sessions 0..PreSeedCount-1) as if backfilled, and load the fakes
        // with the FULL 50-session series so the run-day fetches (past the pre-seed boundary) succeed.
        var wm = $"{Sessions[PreSeedCount - 1]}T22:00:00Z";
        for (var i = 0; i < Sessions.Count; i++)
        {
            AddSeriesPoint(db, MemberA, MemberASymbol, i, preSeed: i < PreSeedCount, wm);
            AddSeriesPoint(db, MemberB, MemberBSymbol, i, preSeed: i < PreSeedCount, wm);
            AddSeriesPoint(db, CwProxy, CwSymbol, i, preSeed: i < PreSeedCount, wm);
            AddProxyPoint(db, i, preSeed: i < PreSeedCount, wm);
        }

        db.SaveChanges();
    }

    private void AddSeriesPoint(AlphaLabDbContext db, long id, string symbol, int i, bool preSeed, string wm)
    {
        var (open, high, low, close) = Ramp(i);
        Market.SetBar(symbol, new EodBar(Sessions[i], open, high, low, close, close, 10_000_000));
        if (preSeed)
        {
            db.Bars.Add(new BarRow
            {
                SecurityId = id, Date = Sessions[i], Version = 1, ObservedAt = wm,
                Open = open, High = high, Low = low, Close = close, AdjClose = close, Volume = 10_000_000, Source = "eodhd",
            });
        }
    }

    private void AddProxyPoint(AlphaLabDbContext db, int i, bool preSeed, string wm)
    {
        var (open, high, low, close) = Ramp(i);
        Proxy.SetBar(new EodBar(Sessions[i], open, high, low, close, close, 1_000_000));
        if (preSeed)
        {
            db.Bars.Add(new BarRow
            {
                SecurityId = RegimeProxy, Date = Sessions[i], Version = 1, ObservedAt = wm,
                Open = open, High = high, Low = low, Close = close, AdjClose = close, Volume = 1_000_000, Source = RegimeProxySource.EodhdGspc,
            });
        }
    }

    // A gentle upward ramp with no overnight gap (open[i] = close[i-1]) so a T+1 fill lands at the target
    // notional and σ is well-defined and positive.
    private (double Open, double High, double Low, double Close) Ramp(int i)
    {
        var close = 100.0 + i * 0.5;
        var open = i == 0 ? close : 100.0 + (i - 1) * 0.5;
        return (open, Math.Max(open, close), Math.Min(open, close), close);
    }

    private static void Register(AlphaLabDbContext db, long id, string symbol) =>
        db.Securities.Add(new SecurityRow { SecurityId = id, CurrentSymbol = symbol, Exchange = "US", FirstSeen = "2024-01-01" });

    private static ConfigRow Config(string key, string value) =>
        new() { Key = key, ValueJson = value, Version = 1, ChangedOn = "2024-01-01", Reason = "test seed" };

    /// <summary>The ISO date of the standard 50-session calendar's session at <paramref name="index"/> —
    /// so a test can compute a clock time BEFORE constructing the harness (e.g. a wider or narrower gap).</summary>
    public static string SessionDate(int index) => BuildSessions(new DateOnly(2024, 1, 1), 50)[index];

    private static IReadOnlyList<string> BuildSessions(DateOnly start, int count)
    {
        var list = new List<string>(count);
        var d = start;
        while (list.Count < count)
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                list.Add(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            d = d.AddDays(1);
        }
        return list;
    }

    private static void EnableWal(string cs)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        // Remove the whole per-instance arena directory (store + -wal/-shm + the backups\ folder).
        try { if (Directory.Exists(_arenaDir)) Directory.Delete(_arenaDir, recursive: true); } catch { /* best effort */ }
    }
}
