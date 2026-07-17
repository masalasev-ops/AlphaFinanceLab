using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Worker;
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

// ---- D53 staged pipeline wiring (checkpoint 2.10) ----
// CONFIG binds (finding F): the CONSUMING phase owns the bind. Register the BOUND options BEFORE
// AddAlphaLabMembership so its TryAddSingleton defaults are no-ops — Data (D77 gate), Calendar,
// CorporateActions (findings B/C), Regime (D50), and Costs (D43) flow from appsettings instead of
// unbound defaults. UniverseOptions stays unregistered on purpose (finding F — wiring it is the
// D70-widening job, not Phase-2 work).
var regimeOptions = builder.Configuration.GetSection(RegimeOptions.SectionName).Get<RegimeOptions>() ?? new RegimeOptions();
var dataQualityOptions = builder.Configuration.GetSection(DataQualityOptions.SectionName).Get<DataQualityOptions>() ?? new DataQualityOptions();
var calendarOptions = builder.Configuration.GetSection(CalendarOptions.SectionName).Get<CalendarOptions>() ?? new CalendarOptions();
var corporateActionsOptions = builder.Configuration.GetSection(CorporateActionsOptions.SectionName).Get<CorporateActionsOptions>() ?? new CorporateActionsOptions();
var costsOptions = builder.Configuration.GetSection(CostsOptions.SectionName).Get<CostsOptions>() ?? new CostsOptions();

builder.Services.AddSingleton(dataQualityOptions);
builder.Services.AddSingleton(calendarOptions);
builder.Services.AddSingleton(corporateActionsOptions);
builder.Services.AddSingleton(costsOptions);
builder.Services.AddAlphaLabMembership(regimeOptions);

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

// The D53 orchestrator + its zero-write Stage-1 fetch. TimeProvider is injectable so run timestamps are
// deterministic under test (never a bare UtcNow in the pipeline body).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<Stage1Fetch>();
builder.Services.AddScoped<DailyPipeline>();

// Catch-up (D47, checkpoint 2.11): the real resolver (runs + bars + calendar + the ET close-time guard)
// and the resumable loop that drives DailyPipeline.RunDayAsync per missed session, oldest first.
builder.Services.AddScoped<IMissedSessionResolver, MissedSessionResolver>();
builder.Services.AddSingleton<CatchupRunner>();

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
