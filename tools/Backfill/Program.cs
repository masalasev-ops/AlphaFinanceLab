using System.Globalization;
using AlphaLab.Data;
using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Strategies;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// AlphaLab bootstrap-backfill CLI (Phase-1, decision #1). Thin composition root: it resolves D67 config,
// migrates the store, wires the concrete providers (the same interfaces BackfillRunner drives offline in
// tests), and runs the backfill. All orchestration + write logic lives in AlphaLab.Data.BackfillRunner so
// the Phase-2 Worker reuses it. The live sp100 run against EODHD/BlackRock is the operator's.

const string Usage =
    "usage: Backfill --universe sp100 [--as-of yyyy-MM-dd] [--years N] [--dry-run] [--preflight]\n" +
    "       Backfill --historical sp500 --from yyyy-MM-dd --to yyyy-MM-dd [--csv path] [--dry-run]   (D70 replay prerequisite)\n" +
    "       Backfill --proxy-only [--as-of yyyy-MM-dd] [--years N] [--dry-run]   (finding 274: GSPC + cap-weight only; extends the regime warm-up + benchmark; membership untouched)";

// D67: config is EXACTLY appsettings.json + appsettings.Secrets.json (optional) — no env vars, no User Secrets.
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: false)
    .Build();

// ---- The D70 HISTORICAL mode (Phase 4, checkpoint 4.3): every historical S&P 500 member inside the
// replay window gets bars + corporate actions, driven by the fja05680 as-of membership. Entirely
// separate from the forward slice bootstrap below — it never touches the forward reconciler.
if (args.Contains("--historical"))
{
    return await RunHistoricalAsync(args, config);
}

// ---- The PROXY-ONLY mode (finding 274): backfill ONLY the regime proxy (GSPC) + cap-weight benchmark
// (OEF), extending them backward for the replay's regime warm-up + benchmark depth, WITHOUT touching the
// membership reconcile (the P1 mass-eviction hazard) or the member universe. Separate from both modes above.
if (args.Contains("--proxy-only"))
{
    return await RunProxyOnlyAsync(args, config);
}

BackfillOptions options;
try
{
    // Config supplies the DEFAULT years; an explicit --years on the command line overrides it (CLI > config).
    var defaultYears = config.GetValue("Backfill:BackfillYears", 20);
    // InvariantCulture: this default AsOf reaches persistence (observed_at, api_usage_log, the slice
    // snapshot's changed_on) — same reasoning as the historical path below (Phase-4 review).
    options = BackfillArgs.Parse(
            args, DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), defaultYears)
        with { ApiPlanLimit = config.GetValue<int?>("Backfill:ApiPlanLimit") };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"argument error: {ex.Message}");
    Console.Error.WriteLine(Usage);
    return 2;
}

// Bridge the layer AlphaLab.Data cannot cross: resolve the cap-weight benchmark ETF proxy (STRATEGY_CATALOG
// §5.1) from the membership source and hand its bare ticker + exchange + config key to the Data-side
// ingestion as strings. Config-driven (never hardcoded): oef_csv ⇒ OEF.US on the D70 slice, ivv_csv ⇒ IVV.US
// at the widen. Fail closed on an unknown source rather than run a backfill with a guessed benchmark.
var membershipPrimary = config["Universe:Bootstrap:MembershipPrimary"] ?? CapWeightProxy.OefSource;
try
{
    options = options with
    {
        CapWeightProxy = CapWeightProxyTarget.FromEodhdSymbol(
            CapWeightProxy.SymbolFor(membershipPrimary), CapWeightProxy.ProxySecurityIdConfigKey, membershipPrimary)
    };
}
catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
{
    Console.Error.WriteLine($"cap-weight proxy config error: {ex.Message}");
    return 2;
}

void Log(string message) => Console.WriteLine(message);

// --preflight (P1R-11): hit every live source once, read-only, and report pass/warn/fail per source. It
// creates NO database and writes nothing (NullRawCache skips even the raw-payload archive), yet exercises the
// SAME providers a live backfill uses — so it proves the real paths, not the fixtures. Precedes --dry-run:
// preflight is the live-source check; dry-run is the config-plan check. Exits non-zero on any Fail.
if (options.Preflight)
{
    var preflightEodhd = new EodhdOptions
    {
        BaseUrl = config["Eodhd:BaseUrl"] ?? "https://eodhd.com/api",
        ExchangeSuffix = config["Eodhd:ExchangeSuffix"] ?? "US",
        // Read the token defensively: a missing/blank token is a Fail on the EODHD checks (the URL 401s),
        // never a crash — the membership/db-path/calendar checks still run and report.
        ApiToken = config["Secrets:EodhdApiToken"] ?? string.Empty
    };
    var preflightHttp = new ResilientHttpClient(new HttpClient());
    var preflightArenaId = config["Arena:Id"] ?? "sp500";
    var preflightConn = config.GetConnectionString("AlphaLab")
        ?? throw new InvalidOperationException("ConnectionStrings:AlphaLab is required in appsettings.json.");
    var preflightAsOf = DateOnly.ParseExact(options.AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    var report = await BackfillPreflight.RunAsync(new PreflightInputs(
        ConnectionString: preflightConn,
        ArenaId: preflightArenaId,
        // NullRawCache.Instance everywhere — preflight archives nothing.
        MembershipPrimary: new ISharesHoldingsMembershipProvider(preflightHttp, ISharesHoldingsOptions.Oef(), NullRawCache.Instance),
        MembershipCrossCheck: new WikipediaMembershipCrossCheck(preflightHttp, new WikipediaMembershipOptions
        {
            Url = config["Backfill:WikipediaSp100Url"] ?? "https://en.wikipedia.org/wiki/S%26P_100",
            Source = "wikipedia_sp100"
        }, NullRawCache.Instance),
        MarketData: new EodhdMarketDataProvider(preflightHttp, preflightEodhd, NullRawCache.Instance),
        RegimeProxy: new EodhdGspcRegimeProxyProvider(preflightHttp, preflightEodhd, NullRawCache.Instance),
        CountBand: options.CountBand,
        ProbeSymbol: "AAPL",
        // Asymmetric probe windows on purpose (see BackfillPreflight): eod/GSPC short (a shape check for
        // adjusted_close / index shape), div wide = options.From (a null unadjustedValue lives in the deep tail).
        EodProbeFrom: preflightAsOf.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DivProbeFrom: options.From,
        AsOf: options.AsOf,
        AsOfYear: preflightAsOf.Year,
        ReviewedThroughYear: NyseCalendar.SpecialClosuresReviewedThroughYear));

    Log($"[preflight] {options.AsOf} — {report.Count} live-source checks (read-only, no DB, no writes):");
    foreach (var r in report)
    {
        Log($"  [{r.Status.ToString().ToUpperInvariant()}] {r.Check}: {r.Detail}");
    }

    var failed = BackfillPreflight.HasFailure(report);
    Log(failed
        ? "[preflight] FAIL — at least one live source has drifted; do not spend on a backfill yet (see above)."
        : "[preflight] OK — every live source looks right.");
    return failed ? 1 : 0;
}

// --dry-run: resolve config + preview the plan. No DB, no network, no writes.
if (options.DryRun)
{
    Log($"[dry-run] {options.PlanSummary()} — no network, no writes.");
    return 0;
}

var arenaId = config["Arena:Id"] ?? "sp500";
var connectionString = config.GetConnectionString("AlphaLab")
    ?? throw new InvalidOperationException("ConnectionStrings:AlphaLab is required in appsettings.json.");

// The CLI is the bootstrap writer (D59 note): it may CREATE a store that does not exist yet, but it must
// never MIGRATE one that does (rule 14; v1.9.17 finding A — the SchemaStartup sibling).
//
// The distinction is the whole point. Creating an absent file loses nothing — there is no data in it, and
// tools/snapshot-db.ps1 explicitly no-ops on a fresh install, so "snapshot first" is vacuous there. This is
// what keeps REBUILD.md §2's documented bootstrap working from a fresh clone. Applying a pending migration
// to an EXISTING store is the opposite: from Phase 2 on it holds the lab's own output (trades, decisions,
// equity_curve) that no provider can re-fetch, so it must go through the snapshot-first path. Refuse and
// name that path (hard rule 10).
var resolved = DbPathResolver.Resolve(connectionString, arenaId);
var dbOptions = new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options;
using var db = new AlphaLabDbContext(dbOptions);

// Checked BEFORE any connection: SQLite creates the file on first connect, so this must precede Migrate().
var storeExisted = File.Exists(new SqliteConnectionStringBuilder(resolved).DataSource);
if (storeExisted)
{
    var pending = db.Database.GetPendingMigrations().ToList();
    if (pending.Count > 0)
    {
        Console.Error.WriteLine(
            $"The store already exists and has {pending.Count} pending migration(s): {string.Join(", ", pending)}.\n" +
            $"Schema is applied ONLY by the snapshot-first path (rule 14) — run:\n" +
            $"  pwsh tools/migrate.ps1 -Arena {arenaId}\n" +
            $"then re-run this backfill.");
        return 1;
    }
}
else
{
    // Fresh install: create the store. Nothing to snapshot.
    db.Database.Migrate();
    Console.WriteLine($"[schema] created a new store for arena '{arenaId}'.");
}

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
    // Enforce the documented raw-cache retention (INTEGRATIONS §9) after a successful run.
    var pruned = rawCache.Prune(DateTime.UtcNow);
    Log($"[cache] pruned {pruned} raw payload(s) older than {FileRawCache.RetentionDays} days.");
    return 0;
}
catch (Exception ex)
{
    // Defensive: the HTTP layer already redacts query strings, but never surface a raw secret-bearing URL.
    Console.Error.WriteLine($"backfill failed: {ex.Message}");
    return 1;
}

// ---- the D70 historical mode (composition only; orchestration lives in HistoricalBackfillRunner) ----
static async Task<int> RunHistoricalAsync(string[] args, IConfiguration config)
{
    HistoricalBackfillOptions options;
    try
    {
        // InvariantCulture is load-bearing (Phase-4 review): on a non-Gregorian default calendar
        // (ar-SA Umm al-Qura) the bare ToString renders a past-shaped year, MembersAsOf(sliceAsOf)
        // matches nothing, and the slice snapshot is silently skipped — the forward universe would
        // widen to ~500 names with no error anywhere.
        options = HistoricalBackfillArgs.Parse(
                args, DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            with
            {
                ApiPlanLimit = config.GetValue<int?>("Backfill:ApiPlanLimit"),
                // Universe:Exclusions (finding 266): symbols skipped on ingest — the same list the replay
                // composition denies from the roster. A future re-run reproduces the exclusion.
                Exclusions = config.GetSection($"{AlphaLab.Data.UniverseOptions.SectionName}:Exclusions").Get<string[]>() ?? [],
            };
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"argument error: {ex.Message}");
        Console.Error.WriteLine("usage: Backfill --historical sp500 --from yyyy-MM-dd --to yyyy-MM-dd [--csv path] [--dry-run]");
        return 2;
    }

    // The as-of membership CSV: an explicit --csv path wins; else Backfill:HistoricalMembershipUrl
    // (a local path or an http(s) URL — the fja05680 community CSV, INTEGRATIONS §4). Fail closed on
    // neither: seeding replay membership from nothing is not a guessable default.
    string csv;
    var csvSource = options.CsvPath ?? config["Backfill:HistoricalMembershipUrl"];
    if (string.IsNullOrWhiteSpace(csvSource))
    {
        Console.Error.WriteLine(
            "historical membership source missing: pass --csv <path> or set Backfill:HistoricalMembershipUrl (the fja05680 CSV).");
        return 2;
    }
    if (csvSource.StartsWith("http", StringComparison.OrdinalIgnoreCase))
    {
        using var httpClient = new HttpClient();
        csv = await httpClient.GetStringAsync(csvSource);
    }
    else
    {
        if (!File.Exists(csvSource))
        {
            Console.Error.WriteLine($"historical membership CSV not found: {csvSource}");
            return 2;
        }
        csv = await File.ReadAllTextAsync(csvSource);
    }

    var arenaId = config["Arena:Id"] ?? "sp500";
    var connectionString = config.GetConnectionString("AlphaLab")
        ?? throw new InvalidOperationException("ConnectionStrings:AlphaLab is required in appsettings.json.");
    var resolved = DbPathResolver.Resolve(connectionString, arenaId);
    using var db = new AlphaLabDbContext(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);

    // Same create-vs-migrate discipline as the forward mode (rule 14 / finding A).
    var storeExists = File.Exists(new SqliteConnectionStringBuilder(resolved).DataSource);
    if (storeExists)
    {
        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count > 0)
        {
            Console.Error.WriteLine(
                $"The store has {pending.Count} pending migration(s): {string.Join(", ", pending)}.\n" +
                $"Run: pwsh tools/migrate.ps1 -Arena {arenaId}  (snapshot-first, rule 14), then re-run.");
            return 1;
        }
    }
    else
    {
        db.Database.Migrate();
        Console.WriteLine($"[schema] created a new store for arena '{arenaId}'.");
    }

    var eodhdOptions = new EodhdOptions
    {
        BaseUrl = config["Eodhd:BaseUrl"] ?? "https://eodhd.com/api",
        ExchangeSuffix = config["Eodhd:ExchangeSuffix"] ?? "US",
        ApiToken = options.DryRun
            ? string.Empty
            : config["Secrets:EodhdApiToken"]
              ?? throw new InvalidOperationException("Secrets:EodhdApiToken is required for a live historical backfill."),
    };
    var histHttp = new ResilientHttpClient(new HttpClient());
    var histCache = new FileRawCache(config["Backfill:RawCacheRoot"] ?? "tools/raw-cache");

    // The TRUE observation instant (D92): a historical backfill observes decades of data NOW.
    options = options with { ObservedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };

    var runner = new HistoricalBackfillRunner(
        db,
        new EodhdMarketDataProvider(histHttp, eodhdOptions, histCache),
        new DataQualityGate(config.GetSection(DataQualityOptions.SectionName).Get<DataQualityOptions>() ?? new DataQualityOptions()),
        Console.WriteLine);

    try
    {
        var report = await runner.RunAsync(options, csv);
        if (options.DryRun) return 0;

        // The durable coverage artifact (D97, user note): a committed file with deterministic content
        // (a re-run over the same store/window/CSV is a clean git diff), at a stable path so reviews
        // and the calibration report can reference it. Sits with the Phase-4 calibration evidence.
        var artifactDir = Path.Combine(config["Backfill:CoverageArtifactDir"] ?? "docs/calibration", arenaId);
        Directory.CreateDirectory(artifactDir);
        var artifactPath = Path.Combine(artifactDir, $"historical-coverage-{options.From}-{options.To}.json");
        await File.WriteAllTextAsync(artifactPath, report.ToCanonicalJson());
        Console.WriteLine($"[artifact] coverage report written: {artifactPath} (sha256 {report.Sha256()[..12]}…).");

        var suspects = report.TickerReuseSuspects.Count;
        var excluded = report.GateExclusions.Count;
        if (suspects + excluded + report.Unresolvable.Count > 0)
        {
            Console.WriteLine(
                $"[review] {excluded} gate-excluded, {suspects} ticker-reuse suspect(s), {report.Unresolvable.Count} " +
                "unresolvable — review the artifact before running replay (excluded names are OUT of the replay universe, fail closed).");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"historical backfill failed: {ex.Message}");
        return 1;
    }
}

// ---- the PROXY-ONLY mode (finding 274; composition only — orchestration is BackfillRunner.BackfillProxiesOnlyAsync) ----
static async Task<int> RunProxyOnlyAsync(string[] args, IConfiguration config)
{
    void Log(string message) => Console.WriteLine(message);

    var asOf = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var years = 25;   // From = AsOf − 25y ≈ 2001 — comfortably past the D73 warm-up floor (≈956 sessions before the 2006-01-03 replay start)
    var dryRun = false;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--as-of" when i + 1 < args.Length: asOf = args[++i]; break;
            case "--years" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out years))
                {
                    Console.Error.WriteLine($"argument error: --years must be an integer, got '{args[i]}'.");
                    return 2;
                }
                break;
            case "--dry-run": dryRun = true; break;
        }
    }
    if (!DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
    {
        Console.Error.WriteLine($"argument error: --as-of must be yyyy-MM-dd, got '{asOf}'.");
        return 2;
    }
    if (years < 24)
    {
        Console.Error.WriteLine(
            $"argument error: --years {years} does not cover the replay window (~20y) plus the D73 regime warm-up (≈4y); need ≥ 24. Refusing (fail closed).");
        return 2;
    }

    // Bridge the layer AlphaLab.Data cannot cross — resolve the cap-weight benchmark ETF proxy from the
    // membership source (identical to the forward mode). Fail closed on an unknown source.
    var membershipPrimary = config["Universe:Bootstrap:MembershipPrimary"] ?? CapWeightProxy.OefSource;
    CapWeightProxyTarget cwProxy;
    try
    {
        cwProxy = CapWeightProxyTarget.FromEodhdSymbol(
            CapWeightProxy.SymbolFor(membershipPrimary), CapWeightProxy.ProxySecurityIdConfigKey, membershipPrimary);
    }
    catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
    {
        Console.Error.WriteLine($"cap-weight proxy config error: {ex.Message}");
        return 2;
    }

    var options = new BackfillOptions
    {
        AsOf = asOf,
        BackfillYears = years,
        DryRun = dryRun,
        RegimeProxySource = config["Regime:ProxySource"] ?? RegimeProxySource.EodhdGspc,
        CapWeightProxy = cwProxy,
        ApiPlanLimit = config.GetValue<int?>("Backfill:ApiPlanLimit"),
    };

    if (dryRun)
    {
        Log($"[dry-run] PROXY-ONLY {options.PlanSummary()} — GSPC + cap-weight only, membership UNTOUCHED, no network, no writes.");
        return 0;
    }

    var arenaId = config["Arena:Id"] ?? "sp500";
    var connectionString = config.GetConnectionString("AlphaLab")
        ?? throw new InvalidOperationException("ConnectionStrings:AlphaLab is required in appsettings.json.");
    var resolved = DbPathResolver.Resolve(connectionString, arenaId);
    using var db = new AlphaLabDbContext(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);

    // Create-vs-migrate discipline (rule 14 / finding A): proxy-only EXTENDS an existing store, so a pending
    // migration goes through the snapshot-first path — never migrate here.
    var storeExists = File.Exists(new SqliteConnectionStringBuilder(resolved).DataSource);
    if (storeExists)
    {
        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count > 0)
        {
            Console.Error.WriteLine(
                $"The store has {pending.Count} pending migration(s): {string.Join(", ", pending)}.\n" +
                $"Run: pwsh tools/migrate.ps1 -Arena {arenaId}  (snapshot-first, rule 14), then re-run.");
            return 1;
        }
    }
    else
    {
        db.Database.Migrate();
        Console.WriteLine($"[schema] created a new store for arena '{arenaId}' — NOTE: proxy-only against a FRESH store warms up a replay that has no members yet; did you mean '--historical' first?");
    }

    var eodhd = new EodhdOptions
    {
        BaseUrl = config["Eodhd:BaseUrl"] ?? "https://eodhd.com/api",
        ExchangeSuffix = config["Eodhd:ExchangeSuffix"] ?? "US",
        ApiToken = config["Secrets:EodhdApiToken"]
            ?? throw new InvalidOperationException("Secrets:EodhdApiToken is required for a live proxy backfill (appsettings.Secrets.json)."),
    };
    var http = new ResilientHttpClient(new HttpClient());
    var rawCache = new FileRawCache(config["Backfill:RawCacheRoot"] ?? "tools/raw-cache");

    // The membership providers are ctor-required but UNUSED by BackfillProxiesOnlyAsync (it never reconciles).
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

    try
    {
        await runner.BackfillProxiesOnlyAsync(options);
        var pruned = rawCache.Prune(DateTime.UtcNow);
        Log($"[cache] pruned {pruned} raw payload(s) older than {FileRawCache.RetentionDays} days.");
        Log("[verify] confirm GSPC has ≥956 distinct sessions before 2006-01-03 (D73 warm-up) and OEF a bar on every replay session, " +
            "then probe that a replay's regime_labels begin at/near 2006-01-03 before the full --reset run.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"proxy-only backfill failed: {ex.Message}");
        return 1;
    }
}
