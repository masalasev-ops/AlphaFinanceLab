using AlphaLab.Worker;
using Microsoft.Extensions.Configuration;

namespace AlphaLab.Worker.Tests;

/// <summary>Mode resolution (D61): default OnDemand; <c>--serve</c> or config selects Scheduled.</summary>
public sealed class WorkerModeParserTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Default_NoArgs_NoConfig_IsOnDemand() =>
        Assert.Equal(WorkerMode.OnDemand, WorkerModeParser.Resolve([], Config()));

    [Fact]
    public void ServeFlag_IsScheduled() =>
        Assert.Equal(WorkerMode.Scheduled, WorkerModeParser.Resolve(["--serve"], Config()));

    [Fact]
    public void ConfigScheduled_IsScheduled() =>
        Assert.Equal(WorkerMode.Scheduled, WorkerModeParser.Resolve([], Config(("Worker:Mode", "Scheduled"))));

    [Fact]
    public void ConfigOnDemand_IsOnDemand() =>
        Assert.Equal(WorkerMode.OnDemand, WorkerModeParser.Resolve([], Config(("Worker:Mode", "OnDemand"))));

    [Fact]
    public void ServeFlag_OverridesConfigOnDemand() =>
        Assert.Equal(
            WorkerMode.Scheduled,
            WorkerModeParser.Resolve(["--serve"], Config(("Worker:Mode", "OnDemand"))));
}
