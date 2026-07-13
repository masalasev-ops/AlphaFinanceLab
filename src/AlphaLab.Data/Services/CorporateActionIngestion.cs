using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>
/// Ingests and TYPES the corporate-action feed (FR-3): EODHD dividends and splits become
/// <c>corporate_actions</c> rows keyed by <c>type</c>. Phase 1 is ingest+type only — <c>processed_on</c>
/// stays NULL until the ledger applies the action in Phase 2 ("Corporate-action semantics complete").
/// Ingestion is idempotent on (security_id, type, effective_date), so a re-run of a backfill never
/// duplicates an event. Dividend cash is the UNADJUSTED per-share amount (the actual cash a holder
/// received) stored as decimal→TEXT (D69); split ratio is REAL.
/// </summary>
public interface ICorporateActionIngestion
{
    /// <summary>Write dividend actions (type='dividend', ex_date = effective_date = event date).
    /// Returns the number of new rows.</summary>
    int IngestDividends(long securityId, IReadOnlyList<DividendEvent> dividends, string observedAt, string source = "eodhd");

    /// <summary>Write split actions (type='split', effective_date = event date, ratio = new/old).
    /// Returns the number of new rows.</summary>
    int IngestSplits(long securityId, IReadOnlyList<SplitEvent> splits, string observedAt, string source = "eodhd");
}

public sealed class CorporateActionIngestion(AlphaLabDbContext db) : ICorporateActionIngestion
{
    public int IngestDividends(long securityId, IReadOnlyList<DividendEvent> dividends, string observedAt, string source = "eodhd")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedAt);
        var inserted = 0;
        foreach (var d in dividends)
        {
            if (string.IsNullOrWhiteSpace(d.Date)) continue;
            if (Exists(securityId, "dividend", d.Date)) continue;

            db.CorporateActions.Add(new CorporateActionRow
            {
                SecurityId = securityId,
                Type = "dividend",
                ExDate = d.Date,          // ex-date = event date (INTEGRATIONS §1)
                EffectiveDate = d.Date,
                CashPerShare = d.UnadjustedValue ?? d.Value, // actual cash per share (D69 decimal→TEXT)
                ObservedAt = observedAt,
                Source = source,
                ProcessedOn = null        // applied by the ledger in Phase 2
            });
            inserted++;
        }
        db.SaveChanges();
        return inserted;
    }

    public int IngestSplits(long securityId, IReadOnlyList<SplitEvent> splits, string observedAt, string source = "eodhd")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedAt);
        var inserted = 0;
        foreach (var s in splits)
        {
            if (string.IsNullOrWhiteSpace(s.Date)) continue;
            if (Exists(securityId, "split", s.Date)) continue;

            db.CorporateActions.Add(new CorporateActionRow
            {
                SecurityId = securityId,
                Type = "split",
                ExDate = null,            // splits carry no ex-date in the feed
                EffectiveDate = s.Date,
                Ratio = s.Ratio,         // new/old (REAL)
                ObservedAt = observedAt,
                Source = source,
                ProcessedOn = null
            });
            inserted++;
        }
        db.SaveChanges();
        return inserted;
    }

    private bool Exists(long securityId, string type, string effectiveDate) =>
        db.CorporateActions.Any(c => c.SecurityId == securityId && c.Type == type && c.EffectiveDate == effectiveDate);
}
