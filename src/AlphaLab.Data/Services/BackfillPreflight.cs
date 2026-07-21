using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>Outcome of one preflight check. <see cref="Warn"/> does not fail the run (exit 0); only
/// <see cref="Fail"/> does.</summary>
public enum PreflightStatus { Pass, Warn, Fail }

/// <summary>One live-source check result: the check's name, its status, and a human-named reason.</summary>
public sealed record PreflightResult(string Check, PreflightStatus Status, string Detail);

/// <summary>Everything <see cref="BackfillPreflight.RunAsync"/> needs — the real providers (so preflight
/// exercises the same code paths a live backfill does), the resolved DB target, and the two probe windows.</summary>
public sealed record PreflightInputs(
    string ConnectionString,
    string ArenaId,
    IIndexMembershipProvider MembershipPrimary,
    IIndexMembershipProvider MembershipCrossCheck,
    IMarketDataProvider MarketData,
    IRegimeProxyProvider RegimeProxy,
    int[] CountBand,
    string ProbeSymbol,
    string EodProbeFrom,
    string DivProbeFrom,
    string AsOf,
    int AsOfYear,
    int ReviewedThroughYear);

/// <summary>
/// P1R-11 (finding 150): the live-source preflight. Between <c>--dry-run</c> (resolves config, zero network)
/// and SETUP §7 (a manual, once-in-2026 ritual) there was nothing — and because every provider test is
/// fixture-backed, <c>ci.ps1</c> stays green while OEF's URL moves or Wikipedia's markup changes. Green means
/// <em>the fixtures parse</em>, not <em>the repo still clones</em>. This makes one pass over every live contact
/// point, read-only, and reports pass/warn/fail per source with a named reason. It creates NO database and
/// writes nothing to the arena store; construct the providers with <see cref="Http.NullRawCache"/> so even the
/// raw-cache archival is skipped. The run exits non-zero on any <see cref="PreflightStatus.Fail"/>.
/// </summary>
public static class BackfillPreflight
{
    public static async Task<IReadOnlyList<PreflightResult>> RunAsync(PreflightInputs inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var band = inputs.CountBand;
        if (band is not { Length: 2 } || band[0] > band[1])
        {
            throw new ArgumentException("CountBand must be a two-element [min,max] with min <= max.", nameof(inputs));
        }

        // Sequential + deterministic ordering: DB path first (cheap, catches a half-applied relocation before
        // any spend), then the four live fetches, then the local calendar-age check.
        return new List<PreflightResult>
        {
            await RunCheck("db-path", _ => Task.FromResult(CheckDbPath(inputs.ConnectionString, inputs.ArenaId)), ct).ConfigureAwait(false),
            await RunCheck("oef-csv (S&P 100 primary)", c => CheckMembership(inputs.MembershipPrimary, band, inputs.AsOf, c), ct).ConfigureAwait(false),
            await RunCheck("wikipedia (S&P 100 cross-check)", c => CheckMembership(inputs.MembershipCrossCheck, band, inputs.AsOf, c), ct).ConfigureAwait(false),
            await RunCheck("eod AAPL.US (adjusted_close)", c => CheckEod(inputs.MarketData, inputs.ProbeSymbol, inputs.EodProbeFrom, inputs.AsOf, c), ct).ConfigureAwait(false),
            await RunCheck("div AAPL.US (unadjustedValue)", c => CheckDiv(inputs.MarketData, inputs.ProbeSymbol, inputs.DivProbeFrom, inputs.AsOf, c), ct).ConfigureAwait(false),
            await RunCheck("eod GSPC.INDX (index shape)", c => CheckGspc(inputs.RegimeProxy, inputs.EodProbeFrom, inputs.AsOf, c), ct).ConfigureAwait(false),
            await RunCheck("calendar-age", _ => Task.FromResult(CheckCalendarAge(inputs.AsOfYear, inputs.ReviewedThroughYear)), ct).ConfigureAwait(false),
        };
    }

    /// <summary>True if any check failed — the caller (the CLI) turns this into a non-zero exit.</summary>
    public static bool HasFailure(IReadOnlyList<PreflightResult> results) =>
        results.Any(r => r.Status == PreflightStatus.Fail);

    // A thrown provider/parse error (e.g. finding 139's FormatException on a null unadjustedValue, a Wikimedia
    // 403, a moved URL, a drifted CSV header) becomes a NAMED Fail here — never an unhandled crash.
    private static async Task<PreflightResult> RunCheck(
        string name, Func<CancellationToken, Task<(PreflightStatus Status, string Detail)>> body, CancellationToken ct)
    {
        try
        {
            var (status, detail) = await body(ct).ConfigureAwait(false);
            return new PreflightResult(name, status, detail);
        }
        catch (Exception ex)
        {
            return new PreflightResult(name, PreflightStatus.Fail, ex.Message);
        }
    }

    // Resolve the connection string + arena id (pure — no directory is created) and confirm a backfill COULD
    // create and write the store, without creating the arena folder. Walk up from the target dir to the first
    // path component that exists: if it is a FILE, the store dir is not creatable (Fail); if it is a directory,
    // probe it with a temp file we immediately delete (write-free w.r.t. the arena store); if nothing on the
    // chain exists (e.g. a missing drive after a half-applied relocation), Fail.
    private static (PreflightStatus, string) CheckDbPath(string connectionString, string arenaId)
    {
        var resolved = DbPathResolver.ResolvePath(connectionString, arenaId); // throws on blank -> caught -> Fail

        // A relative store would give the Worker, the Api, and this CLI a database each, under their own
        // working directories (DB_RELOCATION.md §1). Report it as a preflight Fail with the reason rather
        // than letting Path.GetFullPath below silently root it at the CWD and pass.
        try
        {
            DbPathResolver.RequireAbsoluteStorePath(resolved);
        }
        catch (InvalidOperationException ex)
        {
            return (PreflightStatus.Fail, ex.Message);
        }

        // Reuse the shared extractor (v1.9.36) instead of hand-splitting on ';'/'=': ResolvePath now
        // returns SqliteConnectionStringBuilder output, which QUOTES a value containing ';' or '=', and a
        // naive split would return it truncated and quote-wrapped.
        var dataSource = DbPathResolver.GetDataSourcePath(resolved);
        var targetDir = Path.GetDirectoryName(dataSource);
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return (PreflightStatus.Fail, $"could not derive a store directory from '{dataSource}'.");
        }

        var existing = targetDir;
        while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing) && !File.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
        }

        if (string.IsNullOrEmpty(existing))
        {
            return (PreflightStatus.Fail,
                $"no existing ancestor directory for '{targetDir}' - a backfill could not create the store here " +
                "(a missing drive / half-applied relocation?).");
        }

        if (File.Exists(existing))
        {
            return (PreflightStatus.Fail,
                $"'{existing}' is a file, but the store path needs it to be a directory - '{targetDir}' is not creatable.");
        }

        try
        {
            var probe = Path.Combine(existing, $".alphalab-preflight-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return (PreflightStatus.Fail, $"'{existing}' (nearest existing ancestor of the store dir) is not writable: {ex.Message}");
        }

        return (PreflightStatus.Pass, $"store dir '{targetDir}' is creatable/writable (probed at '{existing}').");
    }

    private static async Task<(PreflightStatus, string)> CheckMembership(IIndexMembershipProvider provider, int[] band, string asOf, CancellationToken ct)
    {
        var snapshot = await provider.GetMembersAsync(asOf, ct).ConfigureAwait(false);
        var n = snapshot.Members.Count;
        return n >= band[0] && n <= band[1]
            ? (PreflightStatus.Pass, $"{n} members, within [{band[0]},{band[1]}].")
            : (PreflightStatus.Fail, $"{n} members, OUTSIDE the count-sanity band [{band[0]},{band[1]}].");
    }

    // Short window on purpose: this is a SHAPE check for adjusted_close; the full backfill depth (o.From, ~20y)
    // would drag ~5,000 bars over the wire to inspect one field. (Asymmetric with the div probe below — see there.)
    private static async Task<(PreflightStatus, string)> CheckEod(IMarketDataProvider md, string symbol, string from, string asOf, CancellationToken ct)
    {
        var bars = await md.GetEodAsync(symbol, from, asOf, asOf, ct).ConfigureAwait(false);
        if (bars.Count == 0)
        {
            return (PreflightStatus.Fail, $"{symbol}: no bars returned over [{from},{asOf}] - endpoint/shape drift.");
        }

        return bars.Any(b => b.AdjClose is not null)
            ? (PreflightStatus.Pass, $"{symbol}: {bars.Count} bars, adjusted_close present.")
            : (PreflightStatus.Fail, $"{symbol}: {bars.Count} bars but NONE carry adjusted_close - the field the split/dividend adjustment depends on.");
    }

    // Wide window on purpose: a null unadjustedValue (finding 139's fail-closed precondition) lives in the deep
    // historical tail, so a short window would probe the least-likely rows and pass vacuously. Use the full
    // backfill depth (o.From) so preflight checks the same deep rows the next backfill will ingest. If the
    // provider has dropped unadjustedValue, ParseDividends throws (finding 139) and RunCheck names it Fail here
    // — before the backfill hits the same throw ~300 calls in.
    private static async Task<(PreflightStatus, string)> CheckDiv(IMarketDataProvider md, string symbol, string from, string asOf, CancellationToken ct)
    {
        var divs = await md.GetDividendsAsync(symbol, from, asOf, ct).ConfigureAwait(false);
        return divs.Count == 0
            ? (PreflightStatus.Pass, $"{symbol}: no dividends over [{from},{asOf}] - unadjustedValue not exercised, but no parse drift.")
            : (PreflightStatus.Pass, $"{symbol}: {divs.Count} dividends, unadjustedValue present on all (else the parse would have thrown).");
    }

    private static async Task<(PreflightStatus, string)> CheckGspc(IRegimeProxyProvider proxy, string from, string asOf, CancellationToken ct)
    {
        var bars = await proxy.GetProxyBarsAsync(from, asOf, asOf, ct).ConfigureAwait(false);
        return bars.Count == 0
            ? (PreflightStatus.Fail, "GSPC.INDX: no index bars returned - index endpoint/shape drift (note: no .US suffix).")
            : (PreflightStatus.Pass, $"GSPC.INDX: {bars.Count} index bars (no .US suffix).");
    }

    private static (PreflightStatus, string) CheckCalendarAge(int asOfYear, int reviewedThroughYear) =>
        asOfYear > reviewedThroughYear
            ? (PreflightStatus.Warn,
                $"as-of year {asOfYear} exceeds NyseCalendar.SpecialClosures' reviewed-through year {reviewedThroughYear}: " +
                "any market closure since is unmodeled and would surface as a false MissingBar gap. Review the closure " +
                "list against exchange notices.")
            : (PreflightStatus.Pass, $"as-of year {asOfYear} is within the closure list's reviewed-through year {reviewedThroughYear}.");

    /// <summary>Pull the <c>Data Source</c> value out of the resolved connection string (SQLite form).</summary>
}
