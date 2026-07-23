using System.Globalization;
using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Json;
using AlphaLab.Data;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Worker.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>The verdict of one reproduction. <see cref="Matches"/> ⇒ every compared output for the day
/// was byte-identical to what the committed run wrote; otherwise <see cref="Differences"/> names the
/// first divergence in each output set.</summary>
public sealed record ReproduceDayOutcome(
    bool Matches,
    string AsOf,
    string Watermark,
    long CommittedRunId,
    IReadOnlyList<string> Differences);

/// <summary>
/// `reproduce-day` — the NFR-1 proof (checkpoint 3.5.1, FR-25). MASTER §13.5 claims "Determinism
/// (NFR1) = f(inputs, watermark, seeds). Any historical run is reproducible forever against exactly
/// the data it saw." This is the thing that makes that claim falsifiable: it re-runs a committed past
/// session from its STORED watermark and seeds, into a throwaway store, and compares the day's output
/// to what was committed, byte for byte.
///
/// READ-ONLY AGAINST THE LIVE ARENA (D59). Every write lands in the <see cref="ScratchStore"/> copy;
/// the live file is opened Mode=ReadOnly for the copy and for the comparison reads. The reproduce run
/// therefore cannot race the Worker and needs no run-in-progress interlock.
///
/// NO NETWORK. The stored-history providers replay Stage 1 from the store's own versioned bars and
/// corporate actions at the run's watermark, so the reproduction needs no EODHD token and cannot be
/// perturbed by a live feed's later revision. Everything else — the orchestrator, the gate, the funnel,
/// the ledger, the populations — is the real, unmodified code composed through
/// <see cref="PipelineComposition.AddDailyPipelineCore"/>.
///
/// WHAT IS COMPARED, AND WHY THOSE FOUR. `decisions.stage_json` (the whole funnel, stage 1 through 6,
/// carrying its own watermark), `trades` filled that day, `equity_curve`, and `control_equity` — the
/// decisions, fills, equity and population draws. They are the outputs with no wall-clock component:
/// `runs.started_at`/`finished_at` and `worker_state.heartbeat_at` are stamped from the clock and are
/// SUPPOSED to differ between two runs of the same day, so including them would make the check fail
/// for a reason that has nothing to do with determinism. Quality flags are also excluded, for a
/// different reason: Stage 1 drops flags on already-gated dates, and the scratch store already holds
/// the day's bars, so a reproduction legitimately re-gates nothing.
/// </summary>
public sealed class ReproduceDayRunner(
    IConfiguration configuration,
    ArenaOptions arena,
    ILoggerFactory loggerFactory)
{
    private const string RunKindLive = "live";
    private const string RunKindCatchup = "catchup";

    private readonly ILogger _logger = loggerFactory.CreateLogger<ReproduceDayRunner>();

    /// <summary>Hook for tests: mutate the scratch store after the rewind and before the re-run, to
    /// prove a perturbed input DIVERGES (a determinism check that only ever passes is not a check).</summary>
    public Action<AlphaLabDbContext>? PerturbScratch { get; set; }

    public async Task<ReproduceDayOutcome> RunAsync(string connectionString, string asOf, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        using var arenaScope = _logger.BeginArenaScope(arena);

        var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
        DbPathResolver.RequireAbsoluteStorePath(resolved);

        string watermark, runKind;
        string? previousSession;
        long committedRunId;
        CommittedDay committed;

        using (var live = OpenReadOnly(resolved))
        {
            var run = FindCommittedRun(live, asOf);
            committedRunId = run.RunId;
            watermark = run.Watermark;
            runKind = run.RunKind;
            previousSession = ResolvePreviousSession(live, asOf);
            committed = CommittedDay.Read(live, asOf);
        }

        _logger.LogInformation(
            "reproduce-day {AsOf}: committed run {RunId} (kind={Kind}) at watermark {Watermark}; " +
            "seeding the book from the {Prev} close.",
            asOf, committedRunId, runKind, watermark, previousSession ?? "(inception)");

        var scratchPath = Path.Combine(
            Path.GetTempPath(),
            $"alphalab-reproduce-{arena.Id}-{asOf}-{Guid.NewGuid():N}.db");

        using var scratch = ScratchStore.CreateRewound(resolved, asOf, previousSession, scratchPath);

        if (PerturbScratch is not null)
        {
            using var perturbCtx = scratch.OpenContext();
            PerturbScratch(perturbCtx);
        }

        await using var provider = BuildScratchServices(scratch, watermark);
        using (var scope = provider.CreateScope())
        {
            var pipeline = scope.ServiceProvider.GetRequiredService<DailyPipeline>();
            // The STORED watermark is passed explicitly (D92): a catchup day's watermark is the true
            // recovery instant, which no re-derivation can reconstruct — reproducing "against exactly
            // the data it saw" means reproducing at exactly the watermark it recorded.
            var result = await pipeline.RunDayAsync(asOf, runKind, watermark, ct).ConfigureAwait(false);
            if (!result.Committed)
            {
                // The reproduction itself failed closed (a Stage-1 gate reject on replayed data). That is
                // a real finding — the day is NOT reproducible — not an excuse to skip the comparison.
                return new ReproduceDayOutcome(false, asOf, watermark, committedRunId,
                    [$"the reproduction did not commit: {result.AbortReason ?? "(no reason recorded)"}"]);
            }
        }

        CommittedDay reproduced;
        using (var scratchCtx = scratch.OpenContext())
        {
            reproduced = CommittedDay.Read(scratchCtx, asOf);
        }

        var differences = committed.DiffAgainst(reproduced);
        if (differences.Count == 0)
        {
            _logger.LogInformation(
                "reproduce-day {AsOf}: BYTE-IDENTICAL. {Decisions} decision(s), {Trades} fill(s), " +
                "{Equity} equity point(s), {Population} population draw(s) reproduced exactly at watermark {Watermark}.",
                asOf, reproduced.Decisions.Count, reproduced.Trades.Count,
                reproduced.Equity.Count, reproduced.Populations.Count, watermark);
        }
        else
        {
            foreach (var d in differences) _logger.LogError("reproduce-day {AsOf}: DIVERGED — {Difference}", asOf, d);
        }

        return new ReproduceDayOutcome(differences.Count == 0, asOf, watermark, committedRunId, differences);
    }

    // ---- the committed run ----

    private static (long RunId, string Watermark, string RunKind) FindCommittedRun(AlphaLabDbContext db, string asOf)
    {
        var run = db.Runs
            .Where(r => r.AsOf == asOf && r.Status == "ok" && (r.RunKind == RunKindLive || r.RunKind == RunKindCatchup))
            .OrderByDescending(r => r.RunId)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No committed forward run for {asOf} (status='ok', run_kind in ('{RunKindLive}','{RunKindCatchup}')). " +
                "reproduce-day proves a day against what was ACTUALLY committed, so there is nothing to prove here " +
                "(fail closed, rule 10).");
        return (run.RunId, run.Watermark, run.RunKind);
    }

    // The session whose closing book seeds the re-run. Uses the SAME calendar service the pipeline
    // uses, so "the previous session" means exactly what it meant on the day.
    private static string? ResolvePreviousSession(AlphaLabDbContext db, string asOf)
    {
        var calendar = new CalendarService(db);
        var date = DateOnly.ParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var previous = calendar.PreviousSession(date);
        return previous is { } p ? p.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    private static AlphaLabDbContext OpenReadOnly(string resolvedConnectionString)
    {
        var readOnly = new SqliteConnectionStringBuilder(resolvedConnectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        return new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(readOnly).Options);
    }

    // ---- the scratch graph: the REAL pipeline, with history replayed from the store ----

    private ServiceProvider BuildScratchServices(ScratchStore scratch, string watermark)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        // ensureDirectory:false — the scratch file already exists and its directory is the temp dir;
        // creating directories is a writer's contract against the ARENA path, which this is not.
        services.AddDailyPipelineCore(configuration, arena, scratch.ConnectionString, ensureDirectory: false);
        services.AddSingleton(configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions());

        // The two axes a reproduction must differ on: history comes from the store at the pinned
        // watermark (no network, no token), and the clock is pinned so the run's own timestamps are
        // deterministic too — they are not compared, but a wandering clock has no place in a
        // determinism proof.
        services.AddSingleton(new StoredHistoryOptions(watermark));
        services.AddScoped<IMarketDataProvider, StoredMarketDataProvider>();
        services.AddScoped<IRegimeProxyProvider, StoredRegimeProxyProvider>();
        services.AddSingleton<TimeProvider>(new PinnedTimeProvider(ParseWatermark(watermark)));

        return services.BuildServiceProvider();
    }

    private static DateTimeOffset ParseWatermark(string watermark) =>
        DateTimeOffset.TryParse(watermark, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

    private sealed class PinnedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>One session's comparable output, read from either store and rendered to canonical JSON.
/// String equality on that rendering IS the byte-identical assertion.</summary>
internal sealed record CommittedDay(
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Trades,
    IReadOnlyList<string> Equity,
    IReadOnlyList<string> Populations)
{
    public static CommittedDay Read(AlphaLabDbContext db, string asOf)
    {
        // Ordered by their natural keys so the comparison is over CONTENT, never over insertion order
        // (rowids differ between the two stores by construction and mean nothing).
        var decisions = db.Decisions
            .Where(d => d.AsOf == asOf && d.RunKind == "live")
            .OrderBy(d => d.AccountId)
            .AsEnumerable()
            .Select(d => Render(new { d.AccountId, d.AsOf, d.StageJson, d.RunKind }))
            .ToList();

        var trades = db.Trades
            .Where(t => t.FilledOn == asOf && t.RunKind == "live")
            .OrderBy(t => t.AccountId).ThenBy(t => t.SecurityId).ThenBy(t => t.Side)
            .AsEnumerable()
            .Select(t => Render(new
            {
                t.AccountId, t.SecurityId, t.Side, t.DecidedOn, t.FilledOn, t.Shares,
                t.RawFillPrice, t.Commission, t.SpreadCost, t.ImpactCost, t.CostModelVersion,
                t.Reason, t.ActionId, t.RunKind,
            }))
            .ToList();

        var equity = db.EquityCurve
            .Where(e => e.AsOf == asOf && e.RunKind == "live")
            .OrderBy(e => e.AccountId)
            .AsEnumerable()
            .Select(e => Render(new { e.AccountId, e.AsOf, e.Equity, e.Cash, e.RunKind }))
            .ToList();

        var populations = db.ControlEquity
            .Where(c => c.AsOf == asOf && c.RunKind == "live")
            .OrderBy(c => c.PopulationId).ThenBy(c => c.MemberIndex)
            .AsEnumerable()
            .Select(c => Render(new { c.PopulationId, c.MemberIndex, c.AsOf, c.Equity, c.RunKind }))
            .ToList();

        return new CommittedDay(decisions, trades, equity, populations);
    }

    public IReadOnlyList<string> DiffAgainst(CommittedDay other)
    {
        var differences = new List<string>();
        Compare("decisions", Decisions, other.Decisions, differences);
        Compare("trades", Trades, other.Trades, differences);
        Compare("equity_curve", Equity, other.Equity, differences);
        Compare("control_equity", Populations, other.Populations, differences);
        return differences;
    }

    private static void Compare(string what, IReadOnlyList<string> committed, IReadOnlyList<string> reproduced, List<string> into)
    {
        if (committed.Count != reproduced.Count)
        {
            into.Add($"{what}: committed {committed.Count} row(s), reproduced {reproduced.Count}");
            return;
        }
        for (var i = 0; i < committed.Count; i++)
        {
            if (!string.Equals(committed[i], reproduced[i], StringComparison.Ordinal))
            {
                into.Add($"{what}: row {i} differs.\n  committed:  {committed[i]}\n  reproduced: {reproduced[i]}");
                return;   // the first divergence is the diagnostic; the rest is noise
            }
        }
    }

    private static string Render(object row) => JsonSerializer.Serialize(row, AlphaLabJson.Options);
}
