using System.Globalization;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>
/// The FORWARD membership refresh (finding 197), run ONCE PER LAUNCH before the catch-up loop.
///
/// **Why launch-scoped rather than per-day.** The holdings providers answer for NOW, never for a past
/// date — INTEGRATIONS §2/§2b drop `asOfDate` from the request deliberately ("the same freeze trap"), so
/// there is exactly ONE current roster per launch and exactly one honest place to apply it. Running it
/// inside `RunDayAsync` would stamp `added_on` with each REPLAYED session's date and fabricate as-of
/// membership for days that already closed. Stamped with today's session instead, the write is invisible
/// to every recovered day by construction: membership reads are half-open `[added_on, removed_on)`, so a
/// row added today cannot alter what any earlier date resolves. That is what makes catch-up safe here
/// rather than merely untested.
///
/// **FORWARD ONLY.** Replay never refreshes: it runs at a frozen watermark (D95) and rule 1 quarantines
/// it from anything that judges strategies, so fetching today's roster into a replay generation would be
/// both a leak and a vintage mix.
///
/// **RULE 12 IS PRESERVED.** The fetch performs zero DB writes, and the apply runs in its OWN small
/// transaction BEFORE the per-day loop opens — never inside a daily write transaction. That is the same
/// shape D72 already established for launch-scoped work ("an OnDemand launch drains queued jobs AFTER
/// catch-up, never inside a daily write transaction").
///
/// **A FAILED REFRESH DOES NOT FAIL THE DAY.** The roster is stable intraday and the stored one is a
/// legitimate input — holding yesterday's state is what the reconciler itself does on a divergence. So a
/// provider outage logs, leaves the store untouched, and lets freshness go stale, which is the honest
/// signal. It is NOT a rule-10 exemption: rule 10 fails closed on a missing RISK input at order time,
/// and the failure here is refusing to CHANGE the universe on unverified data.
/// </summary>
public sealed class MembershipRefreshStep(
    IServiceScopeFactory scopeFactory,
    ILogger<MembershipRefreshStep> logger)
{
    /// <summary>Refresh the roster for <paramref name="today"/> (the current session). Returns the
    /// outcome for logging; never throws on a provider failure.</summary>
    public async Task<MembershipRefreshOutcome> RunAsync(string today, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var universe = sp.GetRequiredService<UniverseOptions>();
        var refresh = sp.GetService<MembershipRefresh>();
        if (refresh is null)
        {
            logger.LogDebug("Membership refresh: no provider composition registered — skipped.");
            return new MembershipRefreshOutcome(null, null, null, "no provider composition");
        }

        // The fetch instant, captured BEFORE the call: this is the number the freshness reading shows,
        // and it is deliberately NOT `today` — telling the fetch instant from the run date is the whole
        // of finding 197.
        var observedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        Data.Providers.MembershipSnapshot primary, cross;
        try
        {
            (primary, cross) = await refresh.FetchAsync(today, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The roster stands and freshness goes stale — the honest outcome, not a failed trading day.
            logger.LogWarning(ex, "Membership refresh: fetch failed; the stored roster stands and freshness will read stale.");
            return new MembershipRefreshOutcome(null, null, null, ex.Message);
        }

        var db = sp.GetRequiredService<AlphaLabDbContext>();
        using var txn = db.Database.BeginTransaction();
        var result = refresh.Apply(primary, cross, today, universe.CountBandFor(universe.Bootstrap.Universe),
            universe.Bootstrap.Universe, observedAt);
        txn.Commit();

        if (result.Applied)
        {
            logger.LogInformation(
                "Membership refresh: applied +{Adds}/-{Drops} (primary={Primary}, cross={Cross}, source={Source}).",
                result.Adds.Count, result.Drops.Count, result.PrimaryCount, result.CrosscheckCount, primary.Source);
        }
        else
        {
            // THE FR-6 DIVERGENCE ALARM'S PRODUCER. The agreed=0 row is already written by the
            // reconciler; this is the operator-facing half.
            logger.LogError(
                "Membership refresh HELD (fail closed): {Reason}. The stored roster stands; inspect index_membership_log.",
                result.HeldReason);
        }

        return new MembershipRefreshOutcome(result, observedAt, primary.Source, null);
    }
}
