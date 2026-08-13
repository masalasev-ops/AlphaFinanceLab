using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker;

/// <summary>What one launch's factor refresh did, for the log line and the caller.</summary>
public sealed record FactorRefreshStepOutcome(FactorRefreshOutcome? Result, string? SkipReason, string? FetchError);

/// <summary>
/// The D41 monthly refresh of the Ken French factor series, run at launch. Deliberately the same shape
/// as <see cref="MembershipRefreshStep"/>, clause for clause, because it is the same KIND of work — an
/// external reference feed that a run READS and a periodic job UPDATES.
///
/// **RULE 12 IS PRESERVED.** The fetch performs zero DB writes (it is a pure
/// <see cref="IFactorDataProvider"/> call), and the apply runs in its OWN small transaction BEFORE the
/// per-day loop opens — never inside a daily write transaction. Same shape D72 established for
/// launch-scoped work.
///
/// **FORWARD ONLY.** Replay runs at a frozen watermark (D95) and rule 1 quarantines it from anything
/// that judges strategies, so fetching today's factor file into a replay generation would be both a
/// vintage mix and a leak.
///
/// **A FAILED REFRESH DOES NOT FAIL THE DAY**, and this is not a rule-10 exemption. Rule 10 fails closed
/// on a missing RISK input at order time; the failure here is a refusal to CHANGE the factor store on
/// unverified data. The stored series stands, freshness goes stale, and the panel says so — which is the
/// honest signal. The *ingest* is what fails closed: <see cref="FactorRefresh"/> writes the fetch whole
/// or writes nothing, never a partial series.
///
/// **MONTHLY, ON A DUE-DATE TEST RATHER THAN A SCHEDULER.** `FactorData:RefreshDayOfMonth` is the due
/// day; the step runs when the last successful refresh is in an earlier month AND today is on or after
/// that day. A launch-scoped due-date test survives the OnDemand default (D61), where there is no
/// resident scheduler to hold a monthly trigger — and the library publishes with weeks of lag, so
/// missing the exact day costs nothing.
/// </summary>
public sealed class FactorRefreshStep(
    IServiceScopeFactory scopeFactory,
    ILogger<FactorRefreshStep> logger)
{
    /// <summary>Refresh if due. Never throws for a provider or data failure.</summary>
    public async Task<FactorRefreshStepOutcome> RunAsync(string today, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var provider = sp.GetService<IFactorDataProvider>();
        if (provider is null)
        {
            logger.LogDebug("Factor refresh: no provider registered — skipped.");
            return new FactorRefreshStepOutcome(null, "no provider registered", null);
        }

        var options = sp.GetRequiredService<FactorDataOptions>();
        var db = sp.GetRequiredService<AlphaLabDbContext>();

        if (!IsDue(db, today, options, out var why))
        {
            logger.LogDebug("Factor refresh: not due ({Why}).", why);
            return new FactorRefreshStepOutcome(null, why, null);
        }

        FactorFetch fetch;
        try
        {
            fetch = await provider.FetchAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stored series stands and freshness goes stale — the honest outcome, not a failed day.
            logger.LogWarning(ex, "Factor refresh: fetch failed; the stored series stands and freshness will read stale.");
            return new FactorRefreshStepOutcome(null, null, ex.Message);
        }

        // The refresh instant, captured AFTER the fetch returns and used as the log row's PK. It is
        // deliberately not `today`: telling the fetch instant from the run date is finding 197's lesson,
        // learned on the membership feed and applying identically here.
        var refreshedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var result = new FactorRefresh(db, options).Apply(fetch, refreshedAt);

        if (result.Written)
        {
            logger.LogInformation(
                "Factor refresh: wrote {Rows} observations through {Through} ({Missing}/{Checked} sessions absent).",
                result.RowsAdded, result.ThroughDate, result.MissingSessions, result.SessionsChecked);
        }
        else
        {
            logger.LogWarning(
                "Factor refresh HELD (the ingest fails closed): {Reason}. The stored series stands.",
                result.Reason);
        }

        return new FactorRefreshStepOutcome(result, null, null);
    }

    /// <summary>Due when no refresh has ever written, or when the last one was in an earlier calendar
    /// month AND today has reached the configured day. Pure on its inputs so the cadence is testable
    /// without a clock.</summary>
    public static bool IsDue(AlphaLabDbContext db, string today, FactorDataOptions options, out string why)
    {
        var last = db.FactorRefreshLog
            .OrderByDescending(r => r.RefreshedAt)
            .Select(r => r.RefreshedAt)
            .FirstOrDefault();

        if (last is null)
        {
            why = "no refresh has ever written";
            return true;
        }

        // Both are ISO, so the yyyy-MM prefix is the calendar month without parsing either.
        var lastMonth = last.Length >= 7 ? last[..7] : last;
        var thisMonth = today.Length >= 7 ? today[..7] : today;
        if (string.CompareOrdinal(thisMonth, lastMonth) <= 0)
        {
            why = $"already refreshed this month ({lastMonth})";
            return false;
        }

        var dayOfMonth = today.Length >= 10 && int.TryParse(today.AsSpan(8, 2), out var d) ? d : 1;
        if (dayOfMonth < options.RefreshDayOfMonth)
        {
            why = $"day {dayOfMonth} is before the configured day {options.RefreshDayOfMonth}";
            return false;
        }

        why = "due";
        return true;
    }
}
