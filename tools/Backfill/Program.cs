using AlphaLab.Data;
using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// AlphaLab bootstrap-backfill CLI (Phase-1, decision #1). Thin composition root: it resolves D67 config,
// migrates the store, wires the concrete providers (the same interfaces BackfillRunner drives offline in
// tests), and runs the backfill. All orchestration + write logic lives in AlphaLab.Data.BackfillRunner so
// the Phase-2 Worker reuses it. The live sp100 run against EODHD/BlackRock is the operator's.

const string Usage = "usage: Backfill --universe sp100 [--as-of yyyy-MM-dd] [--years N] [--dry-run]";

// D67: config is EXACTLY appsettings.json + appsettings.Secrets.json (optional) — no env vars, no User Secrets.
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: false)
    .Build();

BackfillOptions options;
try
{
    // Config supplies the DEFAULT years; an explicit --years on the command line overrides it (CLI > config).
    var defaultYears = config.GetValue("Backfill:BackfillYears", 20);
    options = BackfillArgs.Parse(args, DateTime.UtcNow.ToString("yyyy-MM-dd"), defaultYears)
        with { ApiPlanLimit = config.GetValue<int?>("Backfill:ApiPlanLimit") };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"argument error: {ex.Message}");
    Console.Error.WriteLine(Usage);
    return 2;
}

void Log(string message) => Console.WriteLine(message);

// --dry-run: resolve config + preview the plan. No DB, no network, no writes.
if (options.DryRun)
{
    Log($"[dry-run] {options.PlanSummary()} — no network, no writes.");
    return 0;
}

var arenaId = config["Arena:Id"] ?? "sp500";
var connectionString = config.GetConnectionString("AlphaLab")
    ?? throw new InvalidOperationException("ConnectionStrings:AlphaLab is required in appsettings.json.");

// The CLI is the Phase-1 bootstrap writer (D59 note): resolve the arena-namespaced path (create the dir)
// and ensure the schema exists. Ongoing schema changes still go through tools/migrate.ps1 (snapshot-safe).
var resolved = DbPathResolver.Resolve(connectionString, arenaId);
var dbOptions = new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options;
using var db = new AlphaLabDbContext(dbOptions);
db.Database.Migrate();

// A live backfill needs the EODHD token (hard rule 11 — from the gitignored Secrets file only).
var eodhd = new EodhdOptions
{
    BaseUrl = config["Eodhd:BaseUrl"] ?? "https://eodhd.com/api",
    ExchangeSuffix = config["Eodhd:ExchangeSuffix"] ?? "US",
    ApiToken = config["Secrets:EodhdApiToken"]
        ?? throw new InvalidOperationException("Secrets:EodhdApiToken is required for a live backfill (appsettings.Secrets.json).")
};

var http = new ResilientHttpClient(new HttpClient());
var rawCache = new FileRawCache(config["Backfill:RawCacheRoot"] ?? "tools/raw-cache");

var runner = new BackfillRunner(
    db,
    membershipPrimary: new ISharesHoldingsMembershipProvider(http, ISharesHoldingsOptions.Oef(), rawCache),
    membershipCrossCheck: new WikipediaMembershipCrossCheck(http, new WikipediaMembershipOptions
    {
        Url = config["Backfill:WikipediaSp100Url"] ?? "https://en.wikipedia.org/wiki/S%26P_100",
        Source = "wikipedia_sp100"
    }, rawCache),
    regimeProxy: new EodhdGspcRegimeProxyProvider(http, eodhd, rawCache),
    marketData: new EodhdMarketDataProvider(http, eodhd, rawCache),
    log: Log);

// The forward run is OEF+Wikipedia slice + GSPC proxy + member bars. The fja05680 historical S&P 500
// roster (Backfill:HistoricalMembershipUrl) is a Phase-4 REPLAY prerequisite seeded separately (D70) via
// runner.SeedHistoricalMembershipStep — never chained into the forward RunAsync.
try
{
    await runner.RunAsync(options);
    return 0;
}
catch (Exception ex)
{
    // Defensive: the HTTP layer already redacts query strings, but never surface a raw secret-bearing URL.
    Console.Error.WriteLine($"backfill failed: {ex.Message}");
    return 1;
}
