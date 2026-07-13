using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>Arena identity (FR-37 / D71). Drives the DB path, storage dirs, and log tags.</summary>
public sealed class ArenaOptions
{
    public const string SectionName = "Arena";
    public string Id { get; set; } = "sp500";
    public string DisplayName { get; set; } = "S&P 500";
}

/// <summary>Worker behavior (D61/D72). Phase 0 uses Mode; the D72 timing keys exist but are dormant.</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>OnDemand (default): launch -> catch up -> exit. Scheduled: resident Quartz.</summary>
    public string Mode { get; set; } = "OnDemand";
    public bool ProcessThroughLastCompletedSessionOnly { get; set; } = true;
    public bool DrainQueuedJobsOnLaunch { get; set; } = true;
    public int HeartbeatSeconds { get; set; } = 30;
    public int StaleRunThresholdSeconds { get; set; } = 300;
}

/// <summary>Raised by SchemaStartup to abort host startup with a non-zero exit code.</summary>
public sealed class SchemaStartupException(string message, Exception? inner = null)
    : Exception(message, inner);

internal static class ArenaLog
{
    /// <summary>
    /// Begin a logging scope carrying arena={Arena.Id} (FR-37) around a unit of work. Uses the
    /// message-template overload so console output reads "arena=sp500" while structured sinks still
    /// capture the ArenaId property.
    /// </summary>
    public static IDisposable? BeginArenaScope(this ILogger logger, ArenaOptions arena)
        => logger.BeginScope("arena={ArenaId}", arena.Id);
}
