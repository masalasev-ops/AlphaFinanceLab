using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>
/// The OnDemand launch runner (D61). A BackgroundService, so its ExecuteAsync runs AFTER every
/// hosted-service StartAsync — the schema is guaranteed present (SchemaStartup ran first). It does
/// NOT migrate inline (that would only cover one mode; migration is SchemaStartup's job in both).
///
/// D61/D72: a launch catches up through the last completed session, then exits. Catch-up (D47) IS the
/// work in both modes — the trigger is the only difference. The D72 launch ORDER (stale-run recovery →
/// catch-up → job drain → local backup → exit) lands in checkpoint 2.12; here it is catch-up → exit.
/// </summary>
public sealed class OnDemandRunner(
    CatchupRunner catchup,
    IHostApplicationLifetime lifetime,
    ArenaOptions arena,
    ILogger<OnDemandRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var arenaScope = logger.BeginArenaScope(arena);

        // A crash inside a day rolls that day back (its transaction) but leaves the committed prefix; the
        // exception propagates so the host logs it + exits non-zero, and the next launch resumes there.
        await catchup.RunAsync(stoppingToken);

        lifetime.StopApplication();
    }
}
