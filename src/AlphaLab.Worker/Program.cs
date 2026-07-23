using AlphaLab.Core.Config;
using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Worker;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Pipeline;
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

// ---- ops verbs (FR-25 + Phase 4): `reproduce-day`, `verify-wal`, `replay-calibrate` ----
// Dispatched BEFORE any hosted service is registered, so they never run SchemaStartup (which SETS
// journal_mode — a verifier must not repair what it is checking), never start the heartbeat, and
// never start the OnDemand runner against the live arena. reproduce-day and verify-wal are read-only
// there by construction (reproduce-day writes only in a throwaway copy). replay-calibrate WRITES
// quarantined replay rows — because it bypasses the hosted StaleRunRecovery guard, ReplayRunner runs
// its own sole-writer liveness gate before touching the store (D59; Phase-4 review).
var command = WorkerCommandParser.Parse(args);
if (command.Kind != WorkerCommandKind.Daily)
{
    var commandArena = command.ArenaId is { Length: > 0 } id
        ? new ArenaOptions { Id = id, DisplayName = arena.DisplayName }
        : arena;
    using var opsLoggerFactory = LoggerFactory.Create(b => b
        // EF logs every SQL command at Information by default; the appsettings
        // "Microsoft.EntityFrameworkCore":"Warning" only reaches the resident-host Logging path, NOT this
        // standalone ops factory. Without this filter a full-scale replay-calibrate emits ~20 GB of
        // per-query noise and pays the log-formatting cost on every one of millions of reads (finding 267).
        .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)
        .AddSimpleConsole(o =>
        {
            o.IncludeScopes = true;
            o.SingleLine = true;
        }));
    return await OpsCommandHost.RunAsync(
        command, builder.Configuration, commandArena, connectionString, opsLoggerFactory);
}

builder.Services.AddSingleton(workerOptions);

// Console logging with scopes so the arena tag (FR-37) is visible on every record we emit.
builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
});

// ---- D53 staged pipeline wiring (checkpoint 2.10) ----
// The arena, the store (sole DB writer D59 — so the directory is ensured), every CONFIG bind, the
// membership graph, Stage 1 and the orchestrator. Shared with `reproduce-day` (v1.9.37) so a past
// session is re-run through THIS graph, not a hand-assembled lookalike that could drift from it.
builder.Services.AddDailyPipelineCore(builder.Configuration, arena, connectionString, ensureDirectory: true);

// EODHD provider (finding D — the Worker needs its OWN Eodhd section; CONFIG previously scoped it to
// the Backfill CLI). The token is read DEFENSIVELY (hard rule 11 — the gitignored Secrets file only):
// a missing token is NOT a startup failure, because a no-op launch (nothing to catch up) spends
// nothing; the provider only 401s if a real fetch happens without it.
var eodhd = new EodhdOptions
{
    BaseUrl = builder.Configuration["Eodhd:BaseUrl"] ?? "https://eodhd.com/api",
    ExchangeSuffix = builder.Configuration["Eodhd:ExchangeSuffix"] ?? "US",
    ApiToken = builder.Configuration["Secrets:EodhdApiToken"] ?? string.Empty,
};
builder.Services.AddSingleton(eodhd);
builder.Services.AddScoped<IMarketDataProvider>(sp =>
    new EodhdMarketDataProvider(sp.GetRequiredService<IResilientHttpClient>(), eodhd, sp.GetService<IRawCache>()));
builder.Services.AddScoped<IRegimeProxyProvider>(sp =>
    new EodhdGspcRegimeProxyProvider(sp.GetRequiredService<IResilientHttpClient>(), eodhd, sp.GetService<IRawCache>()));

// TimeProvider is injectable so run timestamps are deterministic under test (never a bare UtcNow in
// the pipeline body); the forward Worker runs on the system clock.
builder.Services.AddSingleton(TimeProvider.System);

// Catch-up (D47, checkpoint 2.11): the real resolver (runs + bars + calendar + the ET close-time guard)
// and the resumable loop that drives DailyPipeline.RunDayAsync per missed session, oldest first.
builder.Services.AddScoped<IMissedSessionResolver, MissedSessionResolver>();
builder.Services.AddSingleton<CatchupRunner>();

// ---- D72 launch order + liveness + backup (checkpoint 2.12) ----
var opsOptions = builder.Configuration.GetSection(OpsOptions.SectionName).Get<OpsOptions>() ?? new OpsOptions();
builder.Services.AddSingleton(opsOptions);

// The worker-liveness reader (AlphaLab.Data): the 409 decision the API reaches via Api->Data in Phase 3.
builder.Services.AddScoped<IWorkerLiveness, WorkerLivenessReader>();

// Launch-order steps (each opens its own scope/txn as needed) + the heartbeat backstop.
builder.Services.AddScoped<HeartbeatWriter>();
builder.Services.AddSingleton<StaleRunRecovery>();
builder.Services.AddSingleton<JobDrainer>();
builder.Services.AddSingleton<LocalBackup>();
// The Phase-4 replay executor (FR-19/FR-32): the API's 202+job_id replay command drains here, after
// catch-up, via the same ReplayRunner the `replay-calibrate` verb drives. The analysis executors
// (briefs/skeptic) still arrive with Phase 5.
builder.Services.AddSingleton<IJobExecutor>(sp => new ReplayJobExecutor(
    builder.Configuration, arena, connectionString, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService<HeartbeatService>();

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
