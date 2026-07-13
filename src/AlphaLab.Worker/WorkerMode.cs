using Microsoft.Extensions.Configuration;

namespace AlphaLab.Worker;

/// <summary>The two Worker run modes (D61).</summary>
public enum WorkerMode
{
    /// <summary>Default: launch -> catch up through the last completed session -> exit.</summary>
    OnDemand,

    /// <summary>Resident: a Quartz schedule triggers the run at session-close + offset (always-on host).</summary>
    Scheduled,
}

/// <summary>
/// Pure resolution of the effective <see cref="WorkerMode"/> from CLI args and config (D61):
/// <c>--serve</c> on the command line OR <c>Worker:Mode = "Scheduled"</c> selects Scheduled; otherwise
/// OnDemand. Side-effect-free so it is unit-testable (the flag overriding config is the interesting case).
/// </summary>
public static class WorkerModeParser
{
    public static WorkerMode Resolve(string[] args, IConfiguration configuration)
    {
        if (args.Any(a => string.Equals(a, "--serve", StringComparison.OrdinalIgnoreCase)))
        {
            return WorkerMode.Scheduled;
        }

        var configured = configuration["Worker:Mode"];
        return string.Equals(configured, nameof(WorkerMode.Scheduled), StringComparison.OrdinalIgnoreCase)
            ? WorkerMode.Scheduled
            : WorkerMode.OnDemand;
    }
}
