using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>
/// Ingests fja05680 historical snapshots into <c>index_membership</c> as as-of intervals for replay
/// reconstruction (FR-4 / D49/D70; feeds FX-AsOfMembership at Phase 4). Given the daily rosters, it
/// derives one <c>(security_id, added_on, removed_on)</c> interval per membership spell — a name
/// added, dropped, then re-added later gets two intervals (distinct <c>added_on</c>). Intervals are
/// half-open <c>[added_on, removed_on)</c>, matching ticker_history. Never deletes.
///
/// Bulk operation (the backfill writer, not the incremental SecurityMaster): all securities are
/// registered in a couple of SaveChanges, then all intervals in one — so a 30-year file ingests fast.
/// Identity is taken from the ticker STRING (canonicalized to EODHD form); ticker-reuse
/// disambiguation (the same symbol reused by a different company across eras) needs point-in-time
/// identity data and is deferred to the live backfill / Phase 4 — a known, logged limitation.
/// </summary>
public interface IHistoricalMembershipIngestion
{
    /// <summary>Reconstruct + write membership intervals. Returns the number of interval rows written.</summary>
    int Ingest(IReadOnlyList<HistoricalMembershipSnapshot> snapshots, string exchange = "US");
}

public sealed class HistoricalMembershipIngestion(AlphaLabDbContext db) : IHistoricalMembershipIngestion
{
    public int Ingest(IReadOnlyList<HistoricalMembershipSnapshot> snapshots, string exchange = "US")
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0) return 0;

        // Canonicalize each roster (EODHD form); keep in chronological order.
        var perDate = snapshots
            .OrderBy(s => s.Date, StringComparer.Ordinal)
            .Select(s => (s.Date, Set: s.RawTickers.Select(SymbolNormalizer.ToEodhd).ToHashSet(StringComparer.Ordinal)))
            .ToList();

        // Earliest appearance per symbol → first_seen.
        var firstSeen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (date, set) in perDate)
        {
            foreach (var sym in set)
            {
                if (!firstSeen.ContainsKey(sym)) firstSeen[sym] = date;
            }
        }

        // Bulk-register securities keyed by canonical symbol (identity from the string; see class note).
        var idBySymbol = db.Securities.ToList()
            .Where(s => s.CurrentSymbol is not null)
            .GroupBy(s => s.CurrentSymbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SecurityId, StringComparer.Ordinal);

        var newSecurities = firstSeen.Keys
            .Where(sym => !idBySymbol.ContainsKey(sym))
            .Select(sym => new SecurityRow { CurrentSymbol = sym, Exchange = exchange, FirstSeen = firstSeen[sym] })
            .ToList();
        if (newSecurities.Count > 0)
        {
            db.Securities.AddRange(newSecurities);
            db.SaveChanges(); // assigns rowids
            foreach (var sec in newSecurities)
            {
                idBySymbol[sec.CurrentSymbol] = sec.SecurityId;
                db.TickerHistory.Add(new TickerHistoryRow
                {
                    SecurityId = sec.SecurityId,
                    Symbol = sec.CurrentSymbol,
                    ValidFrom = firstSeen[sec.CurrentSymbol],
                    ValidTo = null
                });
            }
            db.SaveChanges();
        }

        // Reconstruct intervals by walking the ordered snapshots.
        var open = new Dictionary<long, string>();       // security_id → open interval's added_on
        var intervals = new List<IndexMembershipRow>();
        HashSet<long>? prevIds = null;
        foreach (var (date, set) in perDate)
        {
            var ids = set.Select(sym => idBySymbol[sym]).ToHashSet();
            if (prevIds is null)
            {
                foreach (var id in ids) open[id] = date; // first snapshot: everyone opens here
            }
            else
            {
                foreach (var id in ids)
                {
                    if (!prevIds.Contains(id) && !open.ContainsKey(id)) open[id] = date; // added (or re-added)
                }
                foreach (var id in prevIds)
                {
                    if (!ids.Contains(id) && open.TryGetValue(id, out var addedOn)) // dropped
                    {
                        intervals.Add(new IndexMembershipRow { SecurityId = id, AddedOn = addedOn, RemovedOn = date });
                        open.Remove(id);
                    }
                }
            }
            prevIds = ids;
        }
        foreach (var (id, addedOn) in open) // still-open intervals stay open (removed_on NULL)
        {
            intervals.Add(new IndexMembershipRow { SecurityId = id, AddedOn = addedOn, RemovedOn = null });
        }

        // IDEMPOTENT upsert by the (security_id, added_on) key (Phase 4 / checkpoint 4.3): a re-run of
        // the same CSV derives the same intervals and must write nothing; a LONGER CSV may close a
        // previously-open interval (its removed_on materializes) or open new ones. Blind AddRange
        // double-inserted the PK on any re-run. index_membership is as-of STATE, not an append-only
        // log — updating removed_on here is the same mutation the forward reconciler performs.
        var existing = db.IndexMembership.ToList()
            .ToDictionary(m => (m.SecurityId, m.AddedOn));
        var written = 0;
        foreach (var interval in intervals)
        {
            if (existing.TryGetValue((interval.SecurityId, interval.AddedOn), out var row))
            {
                if (row.RemovedOn != interval.RemovedOn)
                {
                    row.RemovedOn = interval.RemovedOn;
                    written++;
                }
            }
            else
            {
                db.IndexMembership.Add(interval);
                written++;
            }
        }
        db.SaveChanges();
        return written;
    }
}
