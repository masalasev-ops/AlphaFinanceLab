using AlphaLab.Core.Config;
using AlphaLab.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>
/// Runs the read-only ops verbs (`reproduce-day`, `verify-wal`) OUTSIDE the Generic Host, and returns
/// a process exit code (checkpoint 3.5.1/3.5.2, FR-25).
///
/// Deliberately not hosted services. The daily host registers SchemaStartup (which SETS
/// journal_mode=WAL), the heartbeat, and the OnDemand runner (which catches up and writes). None of
/// that may happen on a verb whose entire contract is "look, do not touch" — a `verify-wal` that
/// repaired WAL on its way in could never report the defect it exists to find, and a mistyped verb
/// must never start the sole writer against the live arena. Keeping these off the host makes that
/// structural rather than a matter of registration order.
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
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Not an ops verb."),
        };
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
