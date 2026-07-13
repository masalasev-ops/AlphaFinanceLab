using AlphaLab.Data;
using AlphaLab.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

var builder = Host.CreateApplicationBuilder(args);

// D67: the config builder is EXACTLY appsettings.json + appsettings.Secrets.json (optional).
// No env vars, no User Secrets. Clear the CreateApplicationBuilder defaults, then add the two files.
builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: false);

var arena = builder.Configuration.GetSection(ArenaOptions.SectionName).Get<ArenaOptions>() ?? new ArenaOptions();
var workerOptions = builder.Configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions();
var connectionString = builder.Configuration.GetConnectionString("AlphaLab")
    ?? throw new InvalidOperationException("ConnectionStrings:AlphaLab is required in appsettings.json.");

builder.Services.AddSingleton(arena);
builder.Services.AddSingleton(workerOptions);

// Console logging with scopes so the arena tag (FR-37) is visible on every record we emit.
builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
});

// The Worker is the sole DB writer (D59): resolve the arena-namespaced path AND ensure its directory.
builder.Services.AddAlphaLabData(connectionString, arena.Id, ensureDirectory: true);

// Catch-up resolver (D47) — Phase 0 always resolves to nothing-to-do.
builder.Services.AddScoped<IMissedSessionResolver, Phase0MissedSessionResolver>();

// Schema application + WAL runs in BOTH modes and MUST be registered first (StartAsync runs in
// registration order; do not reorder relative to Quartz / the OnDemand runner).
builder.Services.AddHostedService<SchemaStartup>();

var scheduled = WorkerModeParser.Resolve(args, builder.Configuration) == WorkerMode.Scheduled;

if (scheduled)
{
    // Scheduled (resident) mode: a Quartz stub with no jobs yet — it just keeps the host alive.
    builder.Services.AddQuartz();
    builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
}
else
{
    // OnDemand (default): catch up through the last completed session, then exit.
    builder.Services.AddHostedService<OnDemandRunner>();
}

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AlphaLab.Worker");
using (startupLogger.BeginArenaScope(arena))
{
    startupLogger.LogInformation(
        "AlphaLab.Worker starting. arena={ArenaId} ({ArenaDisplay}), mode={Mode}.",
        arena.Id, arena.DisplayName, scheduled ? "Scheduled" : "OnDemand");
}

try
{
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    startupLogger.LogCritical(ex, "AlphaLab.Worker terminated abnormally.");
    return Environment.ExitCode != 0 ? Environment.ExitCode : 1;
}
