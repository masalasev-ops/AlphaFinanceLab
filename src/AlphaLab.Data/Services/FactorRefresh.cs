using System.Globalization;
using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Services;

/// <summary>The outcome of one refresh. <paramref name="Written"/> false with a
/// <paramref name="Reason"/> is the ONLY failure shape — it never throws for a data problem, because a
/// failed refresh must not fail the day (see <see cref="FactorRefresh"/>).</summary>
public sealed record FactorRefreshOutcome(
    bool Written,
    int RowsAdded,
    string? ThroughDate,
    string Fingerprint,
    int SessionsChecked,
    int MissingSessions,
    string Reason);

/// <summary>
/// The write half of the D41 refresh: validate a <see cref="FactorFetch"/>, then either write it whole
/// or write nothing.
///
/// **"FAIL-CLOSED" BINDS THE INGEST, NOT THE DAY — and three documents had to be reconciled to say so.**
/// The checkpoint text says the refresh is fail-closed; RUNBOOK §38 says a checksum/continuity failure
/// shows a stale-data note with "the trading path is unaffected"; `DataQualityGate` splits gaps (Warn)
/// from unusable values (Reject). They are reconciled the way `MembershipRefreshStep` reconciled the
/// same tension for the universe: **refusing to CHANGE the factor store on unverified data is not a
/// rule-10 exemption**, because rule 10 fails closed on a missing RISK input at order time, and this is
/// a refusal to overwrite good stored data with suspect new data. So: a refusal writes NOTHING — never
/// a partial series — and returns an outcome; it does not throw, and the day proceeds on the series it
/// already had, with freshness going stale, which is the honest signal.
///
/// **WHAT THE FINGERPRINT IS COMPARED AGAINST, stated because a hash with no comparison subject is a
/// check that cannot fail.** French publishes no digest, so this is not verification against an
/// upstream value. It is compared against **the previous refresh's fingerprint**, and it has exactly
/// two jobs:
///   1. **Equal ⇒ nothing to do.** The upstream bytes are unchanged, so the refresh is a no-op and says
///      so, rather than re-deriving and re-writing identical rows.
///   2. **Different ⇒ new bytes, so check what changed.** A changed fingerprint is NOT itself an alarm —
///      it happens every month when a new period is appended. The alarm is the next check.
///
/// **THE REACHABLE REFUSAL IS THE REVISION CHECK, and it is the one that earns the word "checksum".**
/// For every (date, factor) this arena has ALREADY stored, the newly-fetched value must agree. A
/// disagreement means upstream **revised history it had already published** — a real and useful alarm,
/// and the only one that can fire on data that is otherwise well-formed. It refuses the whole refresh.
/// A whole-file hash could never do this: the file legitimately changes every month, so a hash
/// comparison alone would either never fire or fire every time.
///
/// **CONTINUITY IS THE FIRST-FETCH CHECK**, because a first fetch has no prior fingerprint and no stored
/// overlap to differ from. It compares the fetched dates against `trading_calendar` sessions inside the
/// fetched range, tolerating <see cref="FactorDataOptions.MaxMissingSessions"/> — the check exists to
/// catch a TRUNCATED or misaligned file, which fails by orders of magnitude, not by one day.
/// </summary>
public sealed class FactorRefresh(AlphaLabDbContext db, FactorDataOptions options)
{
    /// <summary>Values are decimal daily returns; this is far below any real move and far above
    /// double round-trip error, so it separates "upstream revised it" from "we re-parsed it".</summary>
    private const double RevisionEpsilon = 1e-12;

    public FactorRefreshOutcome Apply(FactorFetch fetch, string refreshedAt)
    {
        ArgumentNullException.ThrowIfNull(fetch);

        var observations = fetch.Observations;
        if (observations.Count == 0)
        {
            return new FactorRefreshOutcome(false, 0, null, fetch.Fingerprint, 0, 0,
                "the fetch produced no observations");
        }

        // ---- 1. fingerprint vs the previous refresh ----
        var priorFingerprint = db.FactorRefreshLog
            .OrderByDescending(r => r.RefreshedAt)
            .Select(r => r.Checksum)
            .FirstOrDefault();

        if (priorFingerprint is not null && string.Equals(priorFingerprint, fetch.Fingerprint, StringComparison.Ordinal))
        {
            return new FactorRefreshOutcome(false, 0, MaxDate(observations), fetch.Fingerprint, 0, 0,
                "upstream bytes unchanged since the last refresh — nothing to do");
        }

        // ---- 2. the revision check: stored history must not have moved ----
        var stored = db.FactorReturns
            .Select(r => new { r.Date, r.Factor, r.Value })
            .ToDictionary(r => (r.Date, r.Factor), r => r.Value);

        foreach (var o in observations)
        {
            if (!stored.TryGetValue((o.Date, o.Factor), out var was)) continue;
            if (Math.Abs(was - o.Value) <= RevisionEpsilon) continue;

            return new FactorRefreshOutcome(false, 0, null, fetch.Fingerprint, 0, 0,
                $"upstream revised published history: {o.Factor} on {o.Date} was " +
                $"{was.ToString("G17", CultureInfo.InvariantCulture)}, now " +
                $"{o.Value.ToString("G17", CultureInfo.InvariantCulture)}. Refusing the whole refresh — " +
                "a revision is a decision about which vintage this arena runs on, not an ingest detail.");
        }

        // ---- 3. continuity across the fetched window ----
        var from = MinDate(observations);
        var through = MaxDate(observations)!;
        var fetchedDates = observations.Select(o => o.Date).ToHashSet(StringComparer.Ordinal);

        var sessions = db.TradingCalendar
            .Where(c => string.Compare(c.Date, from) >= 0 && string.Compare(c.Date, through) <= 0)
            .Select(c => c.Date)
            .ToList();

        var missing = sessions.Count(s => !fetchedDates.Contains(s));
        if (missing > options.MaxMissingSessions)
        {
            return new FactorRefreshOutcome(false, 0, through, fetch.Fingerprint, sessions.Count, missing,
                $"continuity: {missing} of {sessions.Count} trading sessions in [{from}, {through}] have no " +
                $"factor row, above the tolerance of {options.MaxMissingSessions}. A truncated or " +
                "misaligned file fails this way; a calendar disagreement of a day or two does not.");
        }

        // ---- 4. write, whole ----
        var toAdd = observations
            .Where(o => !stored.ContainsKey((o.Date, o.Factor)))
            .Select(o => new FactorReturnRow { Date = o.Date, Factor = o.Factor, Value = o.Value })
            .ToList();

        using var tx = db.Database.BeginTransaction();
        db.FactorReturns.AddRange(toAdd);
        db.FactorRefreshLog.Add(new FactorRefreshLogRow
        {
            RefreshedAt = refreshedAt,
            FilesJson = JsonSerializer.Serialize(fetch.Files),
            Checksum = fetch.Fingerprint,
            RowsAdded = toAdd.Count,
        });
        db.SaveChanges();
        tx.Commit();

        return new FactorRefreshOutcome(true, toAdd.Count, through, fetch.Fingerprint, sessions.Count, missing,
            toAdd.Count == 0 ? "no new observations (the window was already stored)" : "ok");
    }

    private static string? MaxDate(IReadOnlyList<FactorObservation> o) =>
        o.Count == 0 ? null : o.Max(x => x.Date);

    private static string MinDate(IReadOnlyList<FactorObservation> o) => o.Min(x => x.Date)!;
}
