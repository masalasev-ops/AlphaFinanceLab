using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>
/// The OnDemand launch runner (D61). A BackgroundService, so its ExecuteAsync runs AFTER every
/// hosted-service StartAsync — the schema is guaranteed present (SchemaStartup ran first). It does
/// NOT migrate inline (that would only cover one mode; migration is SchemaStartup's job in both).
///
/// Phase 0: resolve missed sessions (D47) — with no trading_calendar and zero committed runs this is
/// nothing-to-do — log it and stop the host so the process exits 0. The real staged pipeline + D47
/// catch-up (and, per D72, the drain-queued-jobs + backup steps) arrive in Phase 2.
/// </summary>
public sealed class OnDemandRunner(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ArenaOptions arena,
    ILogger<OnDemandRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var arenaScope = logger.BeginArenaScope(arena);

        using var scope = scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IMissedSessionResolver>();
        var missed = await resolver.ResolveAsync(stoppingToken);

        if (missed.Count == 0)
        {
            logger.LogInformation(
                "OnDemand launch: no missed sessions to catch up (no trading calendar and no committed runs yet). Exiting cleanly.");
        }
        else
        {
            // Phase 2 replays these in order; Phase 0 never reaches this branch.
            logger.LogInformation("OnDemand launch: {Count} missed session(s) to catch up.", missed.Count);
        }

        lifetime.StopApplication();
    }
}
