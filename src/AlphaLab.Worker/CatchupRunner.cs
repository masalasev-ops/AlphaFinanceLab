using System.Globalization;
using AlphaLab.Worker.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>What one catch-up pass did. <see cref="StoppedEarly"/> ⇒ a fail-closed day halted the run and
/// the committed prefix persists for the next launch to resume from.</summary>
public sealed record CatchupOutcome(int MissedCount, int Processed, bool StoppedEarly, string? StopReason);

/// <summary>
/// The D47 catch-up loop: replay every missed session in order, ONE transaction per day, resumable and
/// idempotent. It drives <see cref="DailyPipeline.RunDayAsync"/> — the daily write — for each session the
/// <see cref="IMissedSessionResolver"/> reports, oldest first.
///
/// RESUMABLE, and the runs table is the state (no state machine): each day is its own transaction, so day
/// k failing leaves 1..k−1 committed and k..n untouched; the next launch's resolver returns the same k..n
/// (a failed day left no 'ok' row and rolled back its bars) and resumes exactly there. A FRESH DbContext
/// per day (a new DI scope) means no cross-day change-tracker state leaks between transactions.
///
/// IDEMPOTENT: ingestion is value-diff-append, so a re-fetch of identical data is a no-op regardless of
/// the watermark; `catchup_log`'s PK and `ux_runs_ok_forward` are the belt-and-braces. The watermark a
/// recovered day records is the TRUE observation instant (D92, finding 194) — never the session-derived
/// `{as_of}T22:00:00Z` fiction — so replay reasons over honest observation dates; a re-run of a failed
/// day records its own (later) honest instant, which is correct, not a determinism leak: reproduce-day
/// pins the watermark the committed run actually stored. No LLM for past days (D47) — structural, there
/// is no IAnalysisProvider until Phase 5.
///
/// run_kind: a session processed on its OWN ET date is 'live'; a session recovered later is 'catchup'
/// (which also writes catchup_log). Both are FORWARD evidence (the ledger collapses them to RunKind.Live).
/// </summary>
public sealed class CatchupRunner(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ArenaOptions arena,
    ILogger<CatchupRunner> logger)
{
    public async Task<CatchupOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        using var arenaScope = logger.BeginArenaScope(arena);

        IReadOnlyList<DateOnly> missed;
        using (var scope = scopeFactory.CreateScope())
        {
            missed = await scope.ServiceProvider.GetRequiredService<IMissedSessionResolver>()
                .ResolveAsync(cancellationToken);
        }

        if (missed.Count == 0)
        {
            logger.LogInformation("Catch-up: no missed sessions — the lab is up to date. Nothing to do.");
            return new CatchupOutcome(0, 0, false, null);
        }

        logger.LogInformation("Catch-up: {Count} missed session(s) to replay in order ({First}..{Last}).",
            missed.Count, Iso(missed[0]), Iso(missed[^1]));

        // Same-ET-day sessions are 'live'; earlier recovered ones are 'catchup'.
        var todayEt = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), EasternTime.Zone).DateTime);

        var processed = 0;
        var recoveredEarlierThisLaunch = false;
        foreach (var day in missed) // ascending — one atomic transaction per day, in order (D47)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runKind = day == todayEt ? "live" : "catchup";

            // Same-launch watermark-inversion guard (Phase-4 review): a launch after 22:00 UTC (the
            // NORMAL evening ET run) that recovers earlier days ingests them at NowIso instants LATER
            // than today's session-derived {asOf}T22:00:00Z fiction. If today then ran 'live' with that
            // fiction, every read on today's run — bars, regime proxy, and the corporate-action applier's
            // (previousSession, asOf] window — would be blind to the rows this very launch just wrote,
            // and a dividend effective in the recovered window would be missed on its ONLY applicable
            // day. So when this launch recovered days first, today runs at its TRUE observation instant
            // too (which sorts after the recovery instants); a pure-live launch keeps the D92 fiction.
            string? watermark = runKind == "live" && recoveredEarlierThisLaunch ? NowIso() : null;

            // Fresh scope ⇒ fresh DbContext for this day's transaction.
            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<DailyPipeline>();
            var result = await pipeline.RunDayAsync(Iso(day), runKind, watermark, ct: cancellationToken);

            if (result.Aborted)
            {
                // A fail-closed gate reject: STOP rather than build later days on a bad day (the T+1 chain
                // depends on it). The committed prefix persists; the next launch resumes here after the
                // data is fixed. A Stage-2 CRASH throws out of RunDayAsync instead and propagates — same
                // resumable outcome (the day rolled back, the prefix is committed).
                logger.LogError(
                    "Catch-up STOPPED at {Day} ({Reason}). {Done} earlier day(s) are committed; re-launch after fixing the data.",
                    Iso(day), result.AbortReason, processed);
                return new CatchupOutcome(missed.Count, processed, true, result.AbortReason);
            }
            processed++;
            if (runKind == "catchup") recoveredEarlierThisLaunch = true;
        }

        logger.LogInformation("Catch-up complete: {Processed}/{Count} session(s) processed.", processed, missed.Count);
        return new CatchupOutcome(missed.Count, processed, false, null);
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string NowIso() => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
