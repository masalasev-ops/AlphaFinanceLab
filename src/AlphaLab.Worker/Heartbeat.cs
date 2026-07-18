using System.Globalization;
using AlphaLab.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>
/// Stamps worker_state.heartbeat_at=now IFF a run is in progress (D72). A SEPARATE connection from the
/// daily transaction: D59's guarantee is one writer PROCESS, not one connection, and SQLite WAL serializes
/// write transactions itself — so this UPDATE simply blocks on the busy timeout and lands the moment the
/// Stage-2 transaction commits. It never beats when idle (run_in_progress=0), so between launches the
/// heartbeat correctly goes stale.
/// </summary>
public sealed class HeartbeatWriter(AlphaLabDbContext db, TimeProvider clock)
{
    /// <summary>Beat once. Returns true if a run was in progress and the timestamp was advanced.</summary>
    public bool Beat()
    {
        var state = db.WorkerState.Find(1);
        if (state is null || state.RunInProgress == 0) return false;
        state.HeartbeatAt = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        db.SaveChanges();
        return true;
    }
}

/// <summary>
/// The liveness heartbeat (D72): a BackgroundService that beats every Worker.HeartbeatSeconds while a run is
/// in progress, on its OWN DI scope (own DbContext + connection). It runs for the whole launch alongside the
/// OnDemand runner; StopApplication at the end of catch-up cancels it. Its beats land BETWEEN the per-day
/// transactions of a long catch-up; DURING a single day's Stage-2 transaction the UPDATE blocks on the write
/// lock and lands only at commit (v1.9.20 finding NN), so within one day the guards against a false
/// stale-positive are the run-open stamp (the small committed txn), the 5× StaleRunThresholdSeconds
/// headroom over the Phase-3 &lt;60s day target, and the pipeline's 3× slow-transaction warning. A tick
/// failure is logged and retried next period, never fatal.
/// </summary>
public sealed class HeartbeatService(
    IServiceScopeFactory scopeFactory,
    WorkerOptions options,
    TimeProvider clock,
    ArenaOptions arena,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var arenaScope = logger.BeginArenaScope(arena);
        var period = TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatSeconds));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    scope.ServiceProvider.GetRequiredService<HeartbeatWriter>().Beat();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Heartbeat tick failed; retrying next period.");
                }
                await Task.Delay(period, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown (StopApplication cancelled the token).
        }
    }
}
