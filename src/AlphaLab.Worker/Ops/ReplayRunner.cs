using System.Globalization;
using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Populations;
using AlphaLab.Worker.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>What a replay run was asked to do (the CLI verb's and the job executor's shared shape).
/// <see cref="LearnThrough"/> is the FR-42 learn/validate split boundary — a RUNTIME parameter, never a
/// CONFIG key; null = no partition (the whole window is learn).</summary>
public sealed record ReplayRequest(
    string From,
    string To,
    string? Watermark = null,
    string? LearnThrough = null,
    bool Reset = false,
    bool WithPlants = true,
    bool WithEvaluation = true);

/// <summary>What one replay pass did. The committed prefix persists on an early stop (resumable: re-run
/// the same command — committed days are skipped).</summary>
public sealed record ReplayOutcome(
    string Watermark,
    int SessionsPlanned,
    int SessionsCommitted,
    int SessionsSkippedAlreadyCommitted,
    bool StoppedEarly,
    string? StopReason);

/// <summary>
/// The Arena Replay engine core (FR-19, D95; DESIGN_IMPROVEMENTS §5): the ENTIRE daily pipeline —
/// funnel, ledger with full corporate-action semantics, populations, gate, monitor, allocator —
/// executed over a historical window, one session at a time, under <c>run_kind='replay'</c>.
///
/// THE D95 WATERMARK CONTRACT (two axes). Version axis: every replay run row carries the FROZEN REAL
/// watermark W_replay = MAX(observed_at) over bars ∪ corporate_actions at replay start — backfilled
/// history has only one observed version (stamped at backfill wall-clock), so an emulated historical
/// watermark would return nothing, and fabricating "2015-shaped" observed_at rows would be exactly the
/// finding-194 fiction D92 outlawed. Date axis ("what was knowable"): bars are date-bounded by every
/// caller, membership resolves as-of the simulated day, and corporate-action reads are bounded by the
/// <see cref="DateCeilingCorporateActionReads"/> decorator — so a 2016-effective merger is invisible to
/// a 2015 simulated day. Determinism stays f(inputs, watermark, seeds): a re-run pinned to the recorded
/// W_replay sees nothing observed later. Replay is thereby slightly LESS informed than a true
/// historical observer (a declared-but-not-yet-effective action is invisible) — the conservative
/// direction, recorded in the calibration report's vintage section.
///
/// ONE GENERATION PER ARENA (D95): replay artifacts under a single watermark form one coherent
/// generation. Mixed vintages would poison the D56 curves, so a run REFUSES if committed replay rows
/// exist at a DIFFERENT watermark (use --reset to delete the old generation — replay-scoped tables
/// only, never bars/corporate_actions/config). Same-watermark committed days are SKIPPED, which is what
/// makes a crashed or stopped replay resumable by re-running the same command.
///
/// THE CLOCK IS REAL. Run-row timestamps and worker_state heartbeats use the system clock: no replay
/// OUTPUT derives from the clock (the watermark is explicit, artifacts are dated by as_of), and a
/// simulated-time heartbeat would read as stale to a concurrent launch's liveness check — which would
/// clear run_in_progress and start the sole writer against a replay mid-write (FR-34's exact hazard).
/// Honest wall-clock provenance on replay runs is a feature, not a leak.
///
/// NO LLM PATH, structurally: this graph registers no IAnalysisProvider / news provider — there is
/// nothing to call (D16/rule 13; FR19_Replay_NoLlmRegistration_Structural pins it).
/// </summary>
public sealed class ReplayRunner(
    IConfiguration configuration,
    ArenaOptions arena,
    ILoggerFactory loggerFactory)
{
    private const string ReplayKind = "replay";

    private readonly ILogger _logger = loggerFactory.CreateLogger<ReplayRunner>();

    /// <summary>Test hook: invoked once per committed day AFTER the pipeline commits it (the plant /
    /// verification checkpoints observe replay state mid-window without re-opening the loop).</summary>
    public Action<string>? AfterDayCommitted { get; set; }

    public async Task<ReplayOutcome> RunAsync(string connectionString, ReplayRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);

        using var arenaScope = _logger.BeginArenaScope(arena);

        var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
        DbPathResolver.RequireAbsoluteStorePath(resolved);

        // Setup reads/writes on a standalone context — the DI graph needs the resolved watermark at
        // construction (StoredHistoryOptions is an immutable singleton), so it is built AFTER this.
        string watermark;
        List<string> sessions;
        HashSet<string> alreadyCommitted;
        using (var db = new AlphaLabDbContext(
                   new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options))
        {
            // Same fail-closed schema rule as every writer (rule 14 / finding A).
            var pending = db.Database.GetPendingMigrations().ToList();
            if (pending.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The store has {pending.Count} pending migration(s) ({string.Join(", ", pending)}) — " +
                    $"run pwsh tools/migrate.ps1 -Arena {arena.Id} first (snapshot-first, rule 14).");
            }

            watermark = request.Watermark ?? ResolveFrozenWatermark(db);
            RecoverCrashedReplayRuns(db);
            if (request.Reset) DeleteReplayGeneration(db);
            GuardSingleGeneration(db, watermark, request);
            if (request.WithPlants) SeedPlants(db, request.From);

            sessions = new CalendarService(db)
                .SessionsBetween(ParseDate(request.From), ParseDate(request.To))
                .Select(Iso)
                .ToList();
            alreadyCommitted = db.Runs
                .Where(r => r.RunKind == ReplayKind && r.Status == "ok")
                .Select(r => r.AsOf)
                .ToHashSet(StringComparer.Ordinal);
        }

        await using var provider = BuildReplayServices(resolved, watermark, request.WithEvaluation);

        if (sessions.Count == 0)
        {
            return new ReplayOutcome(watermark, 0, 0, 0, false, "no sessions in the window (calendar unseeded?)");
        }

        _logger.LogInformation(
            "replay: {Count} session(s) {From}..{To} at frozen watermark {Watermark} ({Skip} already committed will be skipped).",
            sessions.Count, sessions[0], sessions[^1], watermark, sessions.Count(alreadyCommitted.Contains));

        var simDay = provider.GetRequiredService<ReplaySimDay>();
        var committed = 0;
        var skipped = 0;
        foreach (var day in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (alreadyCommitted.Contains(day))
            {
                simDay.Advance(day); // the date axis still moves — a later day must not see less
                skipped++;
                continue;
            }

            simDay.Advance(day);
            using var scope = provider.CreateScope();       // fresh DbContext per day (the CatchupRunner pattern)
            var pipeline = scope.ServiceProvider.GetRequiredService<DailyPipeline>();
            var result = await pipeline.RunDayAsync(day, ReplayKind, watermark, ct).ConfigureAwait(false);

            if (result.Aborted)
            {
                // Fail closed and STOP — later replay days depend on this one (the T+1 chain). The
                // committed prefix persists; re-running the same command resumes here.
                _logger.LogError("replay STOPPED at {Day}: {Reason}. {Done} day(s) committed persist (resumable).",
                    day, result.AbortReason, committed);
                return new ReplayOutcome(watermark, sessions.Count, committed, skipped, true, result.AbortReason);
            }
            committed++;
            AfterDayCommitted?.Invoke(day);
        }

        _logger.LogInformation("replay complete: {Committed} committed, {Skipped} already-committed skipped.", committed, skipped);
        return new ReplayOutcome(watermark, sessions.Count, committed, skipped, false, null);
    }

    // ---- the frozen real watermark: MAX(observed_at) over the two versioned input tables ----
    private static string ResolveFrozenWatermark(AlphaLabDbContext db)
    {
        var barMax = db.Bars.Max(b => (string?)b.ObservedAt);
        var caMax = db.CorporateActions.Max(c => (string?)c.ObservedAt);
        var max = string.CompareOrdinal(barMax, caMax) >= 0 ? barMax : caMax ?? barMax;
        return max ?? throw new InvalidOperationException(
            "The store has no bars — nothing to replay. Run the D70 historical backfill first (fail closed).");
    }

    /// <summary>Seed the D64 plant cohorts (FR-36): one strategies row + one REPLAY account + one
    /// replay-column trials row per plant (D37 — replay trials are the separate registry track).
    /// Idempotent: an existing plant is left untouched (its parameters are frozen in its id). Plants
    /// are FIXTURES seeded directly (like the dummy roster), not CandidateFactory candidates — the
    /// unregistered marker in config_json says so permanently (rule 16).</summary>
    private void SeedPlants(AlphaLabDbContext db, string seededOn)
    {
        var calibration = configuration.GetSection(CalibrationOptions.SectionName).Get<CalibrationOptions>() ?? new CalibrationOptions();
        var populations = configuration.GetSection(PopulationsOptions.SectionName).Get<PopulationsOptions>() ?? new PopulationsOptions();
        var specs = PlantCohorts.Build(calibration.Plant, PopulationFamilies.ForPhase3(populations));

        var seeded = 0;
        foreach (var spec in specs)
        {
            if (!db.Strategies.Any(s => s.StrategyId == spec.StrategyId))
            {
                db.Strategies.Add(new StrategyRow
                {
                    StrategyId = spec.StrategyId,
                    Family = "plant",
                    ConfigJson = JsonSerializer.Serialize(new
                    {
                        plant = true,
                        kind = spec.Kind.ToString().ToLowerInvariant(),
                        family = spec.Family,
                        alpha_ann_pct = spec.AlphaAnnPct,
                        seed = spec.Seed,
                        unregistered = true,
                    }),
                    ExitPolicyJson = "{}",   // a plant never trades; there is no policy to execute
                    HoldingHorizonDays = spec.HorizonDays,
                    CreatedOn = seededOn,
                    Status = "candidate",
                });
                db.TrialsRegistry.Add(new TrialsRegistryRow
                {
                    StrategyId = spec.StrategyId, RegisteredOn = seededOn, Kind = "new", RunKind = ReplayKind,
                });
                seeded++;
            }
            if (!db.Accounts.Any(a => a.StrategyId == spec.StrategyId && a.RunKind == ReplayKind))
            {
                db.Accounts.Add(new AccountRow
                {
                    StrategyId = spec.StrategyId,
                    StartingCash = AlphaLab.Strategies.DummyRoster.DefaultStartingCash,
                    RunKind = ReplayKind,
                });
            }
        }
        db.SaveChanges();
        if (seeded > 0) _logger.LogInformation("replay: seeded {Count} D64 plant(s) across {Total} spec(s).", seeded, specs.Count);
    }

    // A 'running' replay run is a crash orphan: its Stage-2 rolled back, so marking it failed loses
    // nothing and unblocks the generation guard (the StaleRunRecovery idea, replay-scoped).
    private void RecoverCrashedReplayRuns(AlphaLabDbContext db)
    {
        var orphans = db.Runs.Where(r => r.RunKind == ReplayKind && r.Status == "running").ToList();
        if (orphans.Count == 0) return;
        foreach (var run in orphans) run.Status = "failed";
        db.SaveChanges();
        _logger.LogWarning("replay: {Count} orphaned 'running' replay run(s) from a crash marked failed.", orphans.Count);
    }

    private void GuardSingleGeneration(AlphaLabDbContext db, string watermark, ReplayRequest request)
    {
        var existing = db.Runs
            .Where(r => r.RunKind == ReplayKind && r.Status == "ok")
            .Select(r => r.Watermark)
            .Distinct()
            .ToList();
        if (existing.Count == 0) return;
        if (existing.Count == 1 && string.Equals(existing[0], watermark, StringComparison.Ordinal)) return;

        throw new InvalidOperationException(
            $"Committed replay rows exist at watermark(s) [{string.Join(", ", existing)}] but this run resolves " +
            $"{watermark} — mixed replay vintages would poison the D56 calibration curves (D95, one generation " +
            "per arena). Pass --reset to delete the old generation (replay-scoped tables only), or pin " +
            "--watermark to resume the existing one.");
    }

    /// <summary>Delete the replay GENERATION: every run_kind='replay' row plus the replay accounts'
    /// books. Never bars, corporate_actions, config, or any forward row — the CI greps and rule 3 stay
    /// true by construction (no statement here touches the append-only tables).</summary>
    public static void DeleteReplayGeneration(AlphaLabDbContext db)
    {
        using var txn = db.Database.BeginTransaction();

        var replayAccountIds = db.Accounts.Where(a => a.RunKind == ReplayKind).Select(a => a.AccountId).ToList();
        db.Positions.Where(p => replayAccountIds.Contains(p.AccountId)).ExecuteDelete();
        db.CapacityRejections.Where(c => replayAccountIds.Contains(c.AccountId)).ExecuteDelete();

        var replayRunIds = db.Runs.Where(r => r.RunKind == ReplayKind).Select(r => r.RunId).ToList();
        db.DataQualityFlags.Where(f => replayRunIds.Contains(f.RunId)).ExecuteDelete();

        db.Trades.Where(t => t.RunKind == ReplayKind).ExecuteDelete();
        db.CashEvents.Where(c => c.RunKind == ReplayKind).ExecuteDelete();
        db.EquityCurve.Where(e => e.RunKind == ReplayKind).ExecuteDelete();
        db.Decisions.Where(d => d.RunKind == ReplayKind).ExecuteDelete();
        db.PositionSnapshots.Where(p => p.RunKind == ReplayKind).ExecuteDelete();
        db.ControlEquity.Where(c => c.RunKind == ReplayKind).ExecuteDelete();
        db.PowerReports.Where(p => p.RunKind == ReplayKind).ExecuteDelete();
        db.GoLiveLog.Where(g => g.RunKind == ReplayKind).ExecuteDelete();
        db.AllocationLog.Where(a => a.RunKind == ReplayKind).ExecuteDelete();
        db.OverfittingChecks.Where(o => o.RunKind == ReplayKind).ExecuteDelete();
        db.OverfittingStatus.Where(o => o.RunKind == ReplayKind).ExecuteDelete();
        db.TrialsRegistry.Where(t => t.RunKind == ReplayKind).ExecuteDelete();
        db.RegimeLabels.Where(l => l.RunKind == ReplayKind).ExecuteDelete();
        db.RegimeEpisodes.Where(e => e.RunKind == ReplayKind).ExecuteDelete();
        db.ReplayRegimeOutcomes.Where(r => r.RunKind == ReplayKind).ExecuteDelete();
        db.Accounts.Where(a => a.RunKind == ReplayKind).ExecuteDelete();
        db.Runs.Where(r => r.RunKind == ReplayKind).ExecuteDelete();

        txn.Commit();
    }

    // ---- the replay graph: the REAL pipeline over stored history at the frozen watermark ----
    private ServiceProvider BuildReplayServices(string resolvedConnectionString, string watermark, bool withEvaluation)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        // Registered BEFORE AddDailyPipelineCore so its TryAddSingleton default is a no-op: the
        // seeding backtest engine (4.10) is the ONLY caller that disables the evaluation cadence.
        services.AddSingleton(new PipelineEvaluationToggle { Enabled = withEvaluation });
        services.AddDailyPipelineCore(configuration, arena, resolvedConnectionString, ensureDirectory: false);
        services.AddSingleton(configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions());

        // History from the store at the frozen watermark; no network, no token (the ReproduceDay seam,
        // generalized to N days).
        services.AddSingleton(new StoredHistoryOptions(watermark));
        services.AddScoped<IMarketDataProvider, StoredMarketDataProvider>();
        services.AddScoped<IRegimeProxyProvider, StoredRegimeProxyProvider>();
        services.AddSingleton(TimeProvider.System);   // REAL clock — see the class doc

        // The D95 date axis: one simulated-day singleton + the corporate-action ceiling.
        services.AddSingleton<ReplaySimDay>();
        services.AddScoped<ICorporateActionReadService>(sp => new DateCeilingCorporateActionReads(
            new CorporateActionReadService(sp.GetRequiredService<AlphaLabDbContext>()),
            sp.GetRequiredService<ReplaySimDay>()));

        // Replay NEVER runs on the S&P 100 slice (rule 22/D70): the RAW as-of read replaces the
        // forward composition's SliceScopedMembershipRead.
        services.AddScoped<IIndexMembershipRead, IndexMembershipReadService>();

        // The D64 plants (FR-36): the equity step joins the atomic replay day via the extension seam.
        services.AddSingleton(configuration.GetSection(CalibrationOptions.SectionName).Get<CalibrationOptions>() ?? new CalibrationOptions());
        services.AddScoped<IPipelineDayExtension, PlantEquityStep>();

        return services.BuildServiceProvider();
    }

    private static DateOnly ParseDate(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
