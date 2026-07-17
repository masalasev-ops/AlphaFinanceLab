using System.Globalization;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker;

/// <summary>The US Eastern time zone, resolved cross-platform (IANA on Linux/modern Windows, the Windows
/// legacy id as a fallback). The trading calendar's close times are ET, so the "has this session closed
/// yet?" guard must compare in ET — which also handles DST for free (MASTER §20.5: convert so DST never
/// shifts the run relative to the market).</summary>
internal static class EasternTime
{
    public static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        throw new InvalidOperationException(
            "No US Eastern time zone found (tried 'America/New_York' and 'Eastern Standard Time').");
    }
}

/// <summary>
/// The D47 catch-up resolver: which forward sessions still need processing, in order. The runs table is
/// the state — this is a pure read, never a write.
///
/// THE SET (RUNBOOK / D47): every session in <c>[floor, lastCompletedSession]</c> that does NOT already
/// carry a <c>status='ok'</c> FORWARD run, ascending. With floor = the session after the last 'ok' day,
/// every candidate is already non-ok, so the "minus ok days" is BELT-AND-BRACES — `ux_runs_ok_forward`
/// (at most one 'ok' forward row per as_of) is what keeps it sound against any out-of-order run row. A
/// no-op once caught up.
///
///  • <b>floor</b> = the session AFTER the forward-start anchor. The anchor is the last 'ok' forward run's
///    as_of; on a fresh store (no forward runs yet) it falls back to the latest bar date — the backfill
///    watermark — so forward operation begins where the historical data ends, never replaying 20 years.
///    An empty store (no runs AND no bars) has nothing to do.
///  • <b>lastCompletedSession</b> = the latest session whose ET close has PASSED (`now_ET &gt; CloseTime`,
///    NOT close+offset — the offset is Scheduled-mode-only, CONFIG). A session earlier than today is always
///    complete; today's is complete only after its close. Needs the injected clock + the ET zone.
///
/// A failed Stage-2 rolls back its bar ingestion AND leaves no 'ok' row, so neither the anchor nor the
/// exclusion set advances past a failed day — the resolver is self-correcting and the loop resumes there.
/// </summary>
public sealed class MissedSessionResolver(AlphaLabDbContext db, ICalendarService calendar, TimeProvider clock)
    : IMissedSessionResolver
{
    public async Task<IReadOnlyList<DateOnly>> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var lastCompleted = LastCompletedSession();
        if (lastCompleted is null) return [];

        // Sessions already carrying an 'ok' FORWARD run (live|catchup) — the exclusion set (and the anchor).
        var okDays = (await db.Runs
                .Where(r => r.Status == "ok" && (r.RunKind == "live" || r.RunKind == "catchup"))
                .Select(r => r.AsOf)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var lastOk = okDays.Count == 0 ? null : okDays.OrderByDescending(d => d, StringComparer.Ordinal).First();

        // Fresh-store fallback anchor: the latest bar date (backfill watermark). null when there are no bars.
        var maxBarDate = await db.Bars
            .Select(b => b.Date)
            .OrderByDescending(d => d)
            .FirstOrDefaultAsync(cancellationToken);

        var anchor = lastOk ?? (string.IsNullOrEmpty(maxBarDate) ? null : maxBarDate);
        if (anchor is null) return []; // empty store — nothing to catch up

        var floor = calendar.NextSession(ParseDate(anchor));
        if (floor is null) return []; // no session after the anchor in the seeded calendar

        return calendar.SessionsBetween(floor.Value, lastCompleted.Value)
            .Where(d => !okDays.Contains(Iso(d)))
            .ToList();
    }

    // The latest session whose ET close has passed. A pre-today session is always complete; today's is
    // complete only once now_ET is strictly after its ET close (the guard is `>`, so at-close is not yet).
    private DateOnly? LastCompletedSession()
    {
        var nowEt = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), EasternTime.Zone);
        var todayEt = DateOnly.FromDateTime(nowEt.DateTime);

        var candidate = calendar.IsTradingDay(todayEt) ? todayEt : calendar.PreviousSession(todayEt);
        if (candidate is null) return null;

        if (candidate.Value == todayEt && calendar.CloseTime(todayEt) is { } close)
        {
            var closeEt = TimeOnly.ParseExact(close, "HH:mm", CultureInfo.InvariantCulture);
            if (TimeOnly.FromDateTime(nowEt.DateTime) <= closeEt)
            {
                return calendar.PreviousSession(todayEt); // today's close hasn't passed yet
            }
        }
        return candidate;
    }

    private static DateOnly ParseDate(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
