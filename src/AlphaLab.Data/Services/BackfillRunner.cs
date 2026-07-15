using System.Globalization;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>
/// What one backfill run should do. The bootstrap CLI (tools/Backfill) parses these from the command line
/// and drives <see cref="BackfillRunner"/>; the Phase-2 Worker reuses the same runner for catch-up (D59).
/// </summary>
public sealed record BackfillOptions
{
    /// <summary>The forward universe slice to backfill (D70 launch = the S&amp;P 100 OEF slice).</summary>
    public string Universe { get; init; } = "sp100";

    /// <summary>The run/as-of date (ISO yyyy-MM-dd) — the last completed session; reconciliation + config
    /// resolution + the bar-backfill upper bound all anchor here.</summary>
    public string AsOf { get; init; } = default!;

    /// <summary>Years of daily bars + proxy history to pull (CONFIG Data.BackfillYears).</summary>
    public int BackfillYears { get; init; } = 20;

    /// <summary>±this many years around AsOf's year to seed the trading calendar (MASTER §20.5 = ±30y).</summary>
    public int CalendarYearsEitherSide { get; init; } = 30;

    /// <summary>Membership fail-closed count band (D70 slice = [99,103]).</summary>
    public int[] CountBand { get; init; } = [99, 103];

    /// <summary>Regime proxy source (CONFIG Regime.ProxySource).</summary>
    public string RegimeProxySource { get; init; } = Providers.RegimeProxySource.EodhdGspc;

    /// <summary>EODHD daily-call plan limit for the ≥50% headroom check (null = unknown/unlimited).</summary>
    public int? ApiPlanLimit { get; init; }

    /// <summary>Resolve config + walk the plan but make NO network calls and NO writes (decision #1).</summary>
    public bool DryRun { get; init; }

    /// <summary>The bar/proxy backfill lower bound = AsOf − BackfillYears.</summary>
    public string From => DateOnly.ParseExact(AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture)
        .AddYears(-BackfillYears).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The point-in-time watermark stamped on ingested rows (AsOf at a nominal post-close time).</summary>
    public string ObservedAt => $"{AsOf}T22:00:00Z";

    public (int From, int To) CalendarYears
    {
        get
        {
            var year = DateOnly.ParseExact(AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture).Year;
            return (year - CalendarYearsEitherSide, year + CalendarYearsEitherSide);
        }
    }

    /// <summary>One-line preview of what the run would do — printed by <c>--dry-run</c> (no DB, no network).</summary>
    public string PlanSummary()
    {
        var (cf, ct) = CalendarYears;
        return $"universe={Universe} as_of={AsOf} bars {From}..{AsOf} calendar {cf}..{ct} " +
               $"proxy={RegimeProxySource} count-band=[{CountBand[0]},{CountBand[1]}]";
    }
}

/// <summary>Command-line parsing for the bootstrap CLI. Kept here (not in the console) so it is unit-tested.</summary>
public static class BackfillArgs
{
    /// <summary>Parse <c>--universe &lt;name&gt; --as-of &lt;date&gt; --years &lt;n&gt; --dry-run</c>. Every failure throws
    /// <see cref="ArgumentException"/> and fails closed — an unknown flag, a missing/flag-shaped value, a
    /// non-positive/non-integer <c>--years</c>, or an unrecognized universe must NEVER silently run the wrong
    /// backfill. <paramref name="todayIso"/> is the AsOf default (the console passes the last completed
    /// session); <paramref name="defaultYears"/> is the config-supplied default that an explicit
    /// <c>--years</c> overrides.</summary>
    public static BackfillOptions Parse(IReadOnlyList<string> args, string todayIso, int defaultYears = 20)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(todayIso);

        var universe = "sp100";
        var asOf = todayIso;
        var years = defaultYears;
        var dryRun = false;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--universe": universe = RequireValue(args, ref i); break;
                case "--as-of": asOf = RequireValue(args, ref i); break;
                case "--years":
                    var raw = RequireValue(args, ref i);
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out years) || years < 1)
                    {
                        throw new ArgumentException($"--years must be a positive integer (got '{raw}').");
                    }
                    break;
                case "--dry-run": dryRun = true; break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        var countBand = universe switch
        {
            "sp100" => new[] { 99, 103 },
            "sp500" => new[] { 495, 510 },
            _ => throw new ArgumentException($"Unknown universe '{universe}'. Use 'sp100' or 'sp500'.")
        };
        return new BackfillOptions { Universe = universe, AsOf = asOf, BackfillYears = years, DryRun = dryRun, CountBand = countBand };
    }

    // Reject a missing value OR a value that is itself a flag (e.g. `--as-of --dry-run`) — else a requested
    // --dry-run would be swallowed as the as-of value and the run would go LIVE (a fail-open).
    private static string RequireValue(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Missing value for '{args[i]}'.");
        }
        return args[++i];
    }
}

/// <summary>
/// Bootstrap backfill orchestrator (FR-1..6/30/38, decision #1): ties the 1.1–1.9 providers + ingestion
/// services into one run — seed the trading calendar, backfill the regime proxy, refresh + reconcile
/// membership (with sectors), seed historical membership, then backfill each current member's bars +
/// corporate actions. Writes go through the same AlphaLab.Data services the Phase-2 Worker reuses (D59:
/// the CLI is the Phase-1 bootstrap writer). Providers are injected so the whole run is exercised offline
/// against byte-real fixtures; the live sp100 run against EODHD/BlackRock is the operator's (decision #1).
/// Raw payloads are archived by the providers' own <c>IRawCache</c>; this runner counts API calls per
/// source and flushes them to <c>api_usage_log</c> with the ≥50% headroom check (INTEGRATIONS §1).
/// </summary>
public sealed class BackfillRunner(
    AlphaLabDbContext db,
    IIndexMembershipProvider membershipPrimary,
    IIndexMembershipProvider membershipCrossCheck,
    IRegimeProxyProvider regimeProxy,
    IMarketDataProvider marketData,
    Action<string>? log = null)
{
    private const string Exchange = "US";
    private readonly Dictionary<string, int> _apiCalls = new(StringComparer.Ordinal);

    /// <summary>API calls made per source this run (for the api_usage_log flush + headroom check).</summary>
    public IReadOnlyDictionary<string, int> ApiCalls => _apiCalls;

    private void Log(string message) => log?.Invoke(message);
    private void Count(string source, int n = 1) => _apiCalls[source] = _apiCalls.GetValueOrDefault(source) + n;

    // ---- Steps (each idempotent; safe to re-run) ----

    public int SeedCalendarStep(BackfillOptions o)
    {
        var (from, to) = o.CalendarYears;
        var n = new CalendarSeeder(db).Seed(from, to);
        Log($"[calendar] seeded {n} new sessions ({from}..{to}).");
        return n;
    }

    public async Task BackfillRegimeProxyStep(BackfillOptions o, CancellationToken ct = default)
    {
        var id = new RegimeProxyIngestion(db).ResolveProxySecurityId(o.RegimeProxySource, o.AsOf);
        var bars = await regimeProxy.GetProxyBarsAsync(o.From, o.AsOf, o.AsOf, ct).ConfigureAwait(false);
        Count(o.RegimeProxySource);
        var written = new RegimeProxyIngestion(db).IngestProxyBars(id, bars, o.ObservedAt);
        Log($"[regime] proxy security_id={id}; {bars.Count} bars fetched, {written} written.");
    }

    public async Task<MembershipReconcileResult> RefreshMembershipStep(BackfillOptions o, CancellationToken ct = default)
    {
        // Count each fetch the moment it succeeds — not after both — so a cross-check failure still records
        // the primary's spent call (Stage-1 finding: usage accounting must reflect calls actually made).
        var primary = await membershipPrimary.GetMembersAsync(ct).ConfigureAwait(false);
        Count(primary.Source);
        var cross = await membershipCrossCheck.GetMembersAsync(ct).ConfigureAwait(false);
        Count(cross.Source);

        var result = new MembershipReconciler(db, new SecurityMaster(db)).Reconcile(primary, cross, o.AsOf, o.CountBand);
        if (result.Applied)
        {
            ApplySectorsFrom(primary, o.AsOf);
            Log($"[membership] applied: +{result.Adds.Count} / -{result.Drops.Count} (primary={result.PrimaryCount}, cross={result.CrosscheckCount}).");
        }
        else
        {
            Log($"[membership] HELD (fail closed): {result.HeldReason}");
        }
        return result;
    }

    /// <summary>Seed the fja05680 historical S&amp;P 500 roster into <c>index_membership</c> for as-of
    /// reconstruction. This is a REPLAY (Phase-4/D70) prerequisite, invoked SEPARATELY from the forward
    /// <see cref="RunAsync"/> — never chained into the forward sequence (see RunAsync's note).</summary>
    public int SeedHistoricalMembershipStep(string csv)
    {
        var snapshots = HistoricalMembershipCsvParser.Parse(csv);
        var n = new HistoricalMembershipIngestion(db).Ingest(snapshots);
        Log($"[historical] {snapshots.Count} snapshots -> {n} membership intervals.");
        return n;
    }

    public async Task BackfillSecurityStep(long securityId, string symbol, BackfillOptions o, CancellationToken ct = default)
    {
        // Count each call as it returns (not 3-at-once) so a partial security still logs its spent calls.
        // o.AsOf is passed as both the eod query bound (`to`) and the observation day (`asOf`) — they
        // coincide for a backfill, but the archival date is now the explicit asOf, not the bound (P1R-4).
        var bars = await marketData.GetEodAsync(symbol, o.From, o.AsOf, o.AsOf, ct).ConfigureAwait(false);
        Count("eodhd");
        var dividends = await marketData.GetDividendsAsync(symbol, o.From, o.AsOf, ct).ConfigureAwait(false);
        Count("eodhd");
        var splits = await marketData.GetSplitsAsync(symbol, o.From, o.AsOf, ct).ConfigureAwait(false);
        Count("eodhd");

        var barsWritten = new BarIngestionService(db).IngestEod(securityId, bars, o.ObservedAt);
        var ca = new CorporateActionIngestion(db);
        var divWritten = ca.IngestDividends(securityId, dividends, o.ObservedAt);
        var splitWritten = ca.IngestSplits(securityId, splits, o.ObservedAt);

        if (bars.Count == 0)
        {
            // A bootstrap member with no bars is logged, not fatal (the FR-6 quality gate + the daily
            // pipeline's fail-closed handling, Phase 2, act on gaps); never silently pretend it was fine.
            Log($"[security] {symbol} (id={securityId}) returned NO bars — flagged, continuing.");
            return;
        }
        Log($"[security] {symbol} (id={securityId}): {barsWritten} bars, {divWritten} dividends, {splitWritten} splits.");
    }

    /// <summary>Record per-source API call counts to <c>api_usage_log</c> (accumulating), then check the
    /// ≥50% headroom rule (INTEGRATIONS §1) against the AGGREGATE EODHD-family usage — <c>eodhd</c> and
    /// <c>eodhd_gspc</c> share ONE EODHD plan/account, so checking each independently against the full
    /// limit would miss a combined breach. Free sources (BlackRock/Wikipedia) carry no limit. The headroom
    /// check reads the accumulated <b>day total</b> from the DB (not this run's spend), so a breach that
    /// only materializes across multiple same-day runs is still caught (P1R-2). After persisting, the
    /// in-memory counters are drained so a second flush adds zero (exactly-once). Returns the breached
    /// plan buckets.</summary>
    public IReadOnlyList<string> FlushApiUsage(BackfillOptions o)
    {
        var writer = new ApiUsageLogWriter(db);
        foreach (var (source, calls) in _apiCalls)
        {
            var planLimit = IsEodhd(source) ? o.ApiPlanLimit : null;
            writer.Record(o.AsOf, source, calls, planLimit); // each source accumulated separately
        }
        db.SaveChanges();
        _apiCalls.Clear(); // drain: a second FlushApiUsage on this runner adds zero (idempotent)

        // Headroom against the ACCUMULATED EODHD-family day total (from the DB), not this run's spend —
        // so two runs of 2 against a limit of 4 breach on the second, not silently pass (the row was
        // truthful but nothing read it before P1R-2). Arithmetic + family grouping (IsEodhd) unchanged.
        var breached = new List<string>();
        var eodhdTotal = db.ApiUsageLog
            .Where(r => r.AsOf == o.AsOf)
            .AsEnumerable()
            .Where(r => IsEodhd(r.Source))
            .Sum(r => r.Calls);
        if (o.ApiPlanLimit is { } limit && eodhdTotal > 0 && !ApiUsageHeadroom.HasHeadroom(eodhdTotal, limit))
        {
            breached.Add("eodhd");
            Log($"[api] HEADROOM BREACH: EODHD used {eodhdTotal}/{limit} calls day-to-date (<50% headroom).");
        }

        return breached;
    }

    private static bool IsEodhd(string source) => source.StartsWith("eodhd", StringComparison.Ordinal);

    /// <summary>The forward-universe bootstrap sequence (D70 slice = OEF+Wikipedia + GSPC proxy + member
    /// bars). It does NOT seed the fja05680 historical S&amp;P 500 roster — that is a Phase-4 REPLAY prerequisite
    /// (<see cref="SeedHistoricalMembershipStep"/>), seeded separately; co-mingling it here would make a
    /// re-run's forward reconcile mass-evict the historical members (the reconciler is universe-blind, D70/D71).
    /// Idempotent + re-run safe. In <see cref="BackfillOptions.DryRun"/> it logs the plan and makes no network
    /// call or write.</summary>
    public async Task RunAsync(BackfillOptions o, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentException.ThrowIfNullOrWhiteSpace(o.AsOf);

        if (o.DryRun)
        {
            Log($"[dry-run] {o.PlanSummary()} — no network, no writes.");
            return;
        }

        try
        {
            SeedCalendarStep(o);
            await BackfillRegimeProxyStep(o, ct).ConfigureAwait(false);
            await RefreshMembershipStep(o, ct).ConfigureAwait(false);

            var members = new IndexMembershipReadService(db).MembersAsOf(o.AsOf);
            Log($"[members] backfilling {members.Count} current members.");
            foreach (var id in members)
            {
                var symbol = db.Securities.Find(id)?.CurrentSymbol;
                if (symbol is null) continue;
                await BackfillSecurityStep(id, symbol, o, ct).ConfigureAwait(false);
            }

            Log("[done] backfill complete.");
        }
        finally
        {
            // Flush usage even on an aborted run (Stage-1 finding): a mid-run fetch failure still spent
            // calls, and the ≥50% headroom check + daily budget must see them. Guarded so a flush failure
            // never masks the original exception that aborted the run.
            try { FlushApiUsage(o); }
            catch (Exception flushEx) { Log($"[api] usage flush failed (original error preserved): {flushEx.Message}"); }
        }
    }

    // Apply the primary snapshot's GICS sectors to the (now-registered) members.
    private void ApplySectorsFrom(MembershipSnapshot primary, string asOf)
    {
        var master = new SecurityMaster(db);
        var assignments = new List<SectorAssignment>();
        foreach (var m in primary.Members)
        {
            if (string.IsNullOrWhiteSpace(m.Sector)) continue;
            var id = master.ResolveAsOf(m.CanonicalSymbol, Exchange, asOf);
            if (id is { } securityId) assignments.Add(new SectorAssignment(securityId, m.Sector));
        }
        if (assignments.Count > 0) new SectorIngestion(db).ApplySectors(assignments, asOf);
    }
}
