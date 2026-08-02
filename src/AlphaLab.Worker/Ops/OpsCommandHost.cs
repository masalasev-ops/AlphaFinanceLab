using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Recompute;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>
/// Runs the ops verbs (`reproduce-day`, `verify-wal`, `replay-calibrate`, `signal-backfill`,
/// `signal-pin-thresholds`) OUTSIDE the Generic Host, and returns a process exit code
/// (checkpoints 3.5.1/3.5.2 + 4.4–4.8 + Phase-4.5 4.5.2/4.5.3, FR-25/FR-45).
///
/// Deliberately not hosted services. The daily host registers SchemaStartup (which SETS
/// journal_mode=WAL), the heartbeat, and the OnDemand runner (which catches up and writes). None of
/// that may happen on a verb whose contract is "look, do not touch" — a `verify-wal` that repaired
/// WAL on its way in could never report the defect it exists to find, and a mistyped verb must never
/// start the sole writer against the live arena. Keeping these off the host makes that structural.
/// The three WRITING verbs — `replay-calibrate`, `signal-backfill` and `signal-pin-thresholds` —
/// therefore each carry the shared sole-writer liveness gate (<see cref="SoleWriterGate"/>); the
/// hosted StaleRunRecovery guard never runs on this path (D59/D72).
/// </summary>
public static class OpsCommandHost
{
    public static async Task<int> RunAsync(
        WorkerCommand command,
        IConfiguration configuration,
        ArenaOptions arena,
        string connectionString,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Kind switch
        {
            WorkerCommandKind.ReproduceDay =>
                await ReproduceAsync(command, configuration, arena, connectionString, loggerFactory, ct).ConfigureAwait(false),
            WorkerCommandKind.VerifyWal =>
                VerifyWal(arena, connectionString, loggerFactory),
            WorkerCommandKind.ReplayCalibrate =>
                await ReplayCalibrateAsync(command, configuration, arena, connectionString, loggerFactory, ct).ConfigureAwait(false),
            WorkerCommandKind.SignalBackfill =>
                await SignalBackfillAsync(command, configuration, arena, connectionString, loggerFactory, ct).ConfigureAwait(false),
            WorkerCommandKind.SignalPinThresholds =>
                SignalPinThresholds(command, configuration, arena, connectionString, loggerFactory),
            WorkerCommandKind.PinProposalThresholds =>
                PinProposalThresholds(command, configuration, arena, connectionString, loggerFactory),

            WorkerCommandKind.ReplayRecompute =>
                ReplayRecompute(command, configuration, arena, connectionString, loggerFactory),
            WorkerCommandKind.StoreSweep =>
                StoreSweep(configuration, arena, connectionString, loggerFactory),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Not an ops verb."),
        };
    }

    // The FR-45 signal-IC backfill (checkpoint 4.5.3). WRITES `signal_ic`, so it runs here in the
    // Worker process (the sole writer, D59) and carries its own liveness gate. It refuses to grade a
    // single day until the D108 thresholds are pinned — that refusal is reported as its own exit path
    // rather than folded into the generic failure, because "not pinned yet" is an ORDERING message the
    // operator can act on, not a crash.
    private static async Task<int> SignalBackfillAsync(
        WorkerCommand command,
        IConfiguration configuration,
        ArenaOptions arena,
        string connectionString,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("AlphaLab.Worker.SignalBackfill");
        try
        {
            var outcome = await new SignalBackfillRunner(configuration, arena, loggerFactory)
                .RunAsync(connectionString, command.SignalBackfill!, ct).ConfigureAwait(false);
            logger.LogInformation(
                "signal-backfill: {Written} grade(s) written over {Graded}/{Planned} session(s) in {Elapsed}.",
                outcome.GradesWritten, outcome.SessionsGraded, outcome.SessionsPlanned, outcome.Elapsed);
            return 0;
        }
        catch (SignalThresholdsNotPinnedException ex)
        {
            logger.LogCritical("signal-backfill refused: {Message}", ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "signal-backfill could not run.");
            return 1;
        }
    }

    // The Phase-4 replay + calibration chain (checkpoints 4.4–4.8). NOT read-only: replay WRITES
    // quarantined run_kind='replay' rows to the arena — which is exactly why it runs here, in the
    // Worker process (the sole writer, D59), and not in the API or a separate tool. The full chain:
    // replay → curves (learn period) → C-1 sweep → FR-41 → verification → archived report → config
    // freeze (unless --report-only).
    private static async Task<int> ReplayCalibrateAsync(
        WorkerCommand command,
        IConfiguration configuration,
        ArenaOptions arena,
        string connectionString,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("AlphaLab.Worker.ReplayCalibrate");
        try
        {
            return await new CalibrationOrchestrator(configuration, arena, loggerFactory)
                .RunAsync(connectionString, command.Replay!, command.ReportOnly, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "replay-calibrate could not run.");
            return 1;
        }
    }

    // Checkpoint 4.5.2 (D108): pin the two trend-flag significance levels as versioned config rows. A
    // WRITING verb, so it runs here with its own liveness gate. It exists because the FR-45 backfill's
    // pin refusal had no sanctioned way to be satisfied otherwise — rule 15 forbids hand-editing the
    // store, and every other config write is a code path that owns its own value.
    private static int SignalPinThresholds(
        WorkerCommand command,
        IConfiguration configuration,
        ArenaOptions arena,
        string connectionString,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AlphaLab.Worker.SignalPinThresholds");
        try
        {
            var outcome = new SignalThresholdPinner(configuration, arena, loggerFactory)
                .Run(connectionString, command.SignalPin!);
            if (outcome.Written.Count == 0)
            {
                logger.LogInformation(
                    "signal-pin-thresholds: nothing written — all {Count} key(s) were already pinned.",
                    outcome.AlreadyPinned.Count);
            }
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "signal-pin-thresholds could not run.");
            return 1;
        }
    }

    // The two D110 proposal-score parameters (checkpoint 5.7). Same shape as SignalPinThresholds and for
    // the same reason: the hypotheses endpoint refuses while either is unpinned, and rule 15 leaves no
    // other legitimate way to satisfy that refusal than a verb that owns the write.
    private static int PinProposalThresholds(
        WorkerCommand command,
        IConfiguration configuration,
        ArenaOptions arena,
        string connectionString,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AlphaLab.Worker.PinProposalThresholds");
        try
        {
            var outcome = new ProposalThresholdPinner(configuration, arena, loggerFactory)
                .Run(connectionString, command.ProposalPin!);
            if (outcome.Written.Count == 0)
            {
                logger.LogInformation(
                    "pin-proposal-thresholds: nothing written — all {Count} key(s) were already pinned.",
                    outcome.AlreadyPinned.Count);
            }
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "pin-proposal-thresholds could not run.");
            return 1;
        }
    }

    // The D106/D117 recompute harness. REPORT-ONLY, so unlike every other write-capable verb here it needs
    // no writer guard and takes no transaction — it reads the stored generation and writes one markdown
    // artefact. A parity FAILURE is a non-zero exit: §25.3 makes it a stop condition ("the harness is not
    // used for its purpose and generation 2 stands"), and an operator who scripted this must not read a
    // failed parity as a successful run.
    private static int ReplayRecompute(
        WorkerCommand command,
        IConfiguration configuration,
        ArenaOptions arena,
        string connectionString,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AlphaLab.Worker.ReplayRecompute");
        try
        {
            var request = command.Recompute!;
            var spec = new RecomputeSpec(request.SpecName ?? (request.Overrides.Count == 0 ? "parity" : "candidate"),
                request.Overrides);
            var gate = configuration.GetSection(GateOptions.SectionName).Get<GateOptions>() ?? new GateOptions();

            // The configured string is a TEMPLATE carrying the FR-37 `{Arena.Id}` token — resolve it the
            // same way every other ops verb does, or the harness opens a path that does not exist and the
            // failure reads like a missing store rather than a missing substitution.
            var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
            DbPathResolver.RequireAbsoluteStorePath(resolved);

            using var db = new AlphaLabDbContext(
                new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);

            var run = new RecomputeOrchestrator(db, gate, arena, loggerFactory.CreateLogger<RecomputeOrchestrator>())
                .Run(spec, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            if (request.VerifyParity && !run.Report.ParityHolds)
            {
                logger.LogError(
                    "replay-recompute: FX-RecomputeParity FAILED. Per MASTER §25.3 the harness is not used " +
                    "for its purpose and generation 2 stands — the equality is never relaxed to a tolerance. " +
                    "Investigate which input is impure. Report: {Path}", run.ReportPath);
                return 1;
            }
            return 0;
        }
        catch (RecomputeRefusedException ex)
        {
            // A refusal is the SPECIFIED behaviour for a specification the harness cannot honestly answer
            // (§25.2), not a crash — logged as such so it is not mistaken for one.
            logger.LogError("replay-recompute refused: {Message}", ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "replay-recompute could not run.");
            return 1;
        }
    }

    // The D120 stored-corpus quality sweep. REPORT-ONLY like replay-recompute: no writer guard, no
    // transaction — it reads the stored corpus and writes one markdown artefact naming the securities
    // recommended for Universe:Exclusions. A non-empty recommendation is exit 0, not an error: finding
    // garbage is the verb's PRODUCT, and the operator acts on the report, not on the exit code.
    private static int StoreSweep(
        IConfiguration configuration,
        ArenaOptions arena,
        string connectionString,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AlphaLab.Worker.StoreSweep");
        try
        {
            var dataOptions = configuration.GetSection(DataQualityOptions.SectionName)
                .Get<DataQualityOptions>() ?? new DataQualityOptions();

            var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
            DbPathResolver.RequireAbsoluteStorePath(resolved);

            using var db = new AlphaLabDbContext(
                new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);

            new StoreSweepOrchestrator(db, dataOptions, arena, loggerFactory.CreateLogger<StoreSweepOrchestrator>())
                .Run(DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "store-sweep could not run.");
            return 1;
        }
    }

    private static async Task<int> ReproduceAsync(
        WorkerCommand command,
        IConfiguration configuration,
        ArenaOptions arena,
        string connectionString,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("AlphaLab.Worker.ReproduceDay");
        var runner = new ReproduceDayRunner(configuration, arena, loggerFactory);
        try
        {
            var outcome = await runner.RunAsync(connectionString, command.Date!, ct).ConfigureAwait(false);
            if (outcome.Matches)
            {
                logger.LogInformation(
                    "reproduce-day {AsOf}: PASS — the day reproduces byte-identically from watermark {Watermark} (NFR-1).",
                    outcome.AsOf, outcome.Watermark);
                return 0;
            }

            logger.LogError(
                "reproduce-day {AsOf}: FAIL — {Count} output set(s) diverged from committed run {RunId}.",
                outcome.AsOf, outcome.Differences.Count, outcome.CommittedRunId);
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "reproduce-day {AsOf} could not run.", command.Date);
            return 1;
        }
    }

    private static int VerifyWal(ArenaOptions arena, string connectionString, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AlphaLab.Worker.VerifyWal");
        using var arenaScope = logger.BeginArenaScope(arena);
        try
        {
            var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
            DbPathResolver.RequireAbsoluteStorePath(resolved);

            var path = DbPathResolver.GetDataSourcePath(resolved);
            if (!File.Exists(path))
            {
                logger.LogCritical("verify-wal: no store at '{Path}'. Nothing to verify (fail closed).", path);
                return 1;
            }

            using var db = new AlphaLabDbContext(
                new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);
            var result = WalVerification.Verify(db);

            if (!result.Ok)
            {
                logger.LogCritical(
                    "verify-wal FAILED for '{Path}': {Reason} (journal_mode={Mode}).",
                    path, result.FailureReason, result.JournalMode);
                return 1;
            }

            logger.LogInformation(
                "verify-wal OK for '{Path}': journal_mode={Mode}, checkpoint completed ({Checkpointed}/{WalPages} page(s)).",
                path, result.JournalMode, result.CheckpointedPages, result.WalPages);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "verify-wal could not run.");
            return 1;
        }
    }
}
