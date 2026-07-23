using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>
/// Ingests and TYPES the corporate-action feed (FR-3): EODHD dividends and splits become
/// <c>corporate_actions</c> rows keyed by <c>type</c> — ingest+type only; the ledger APPLIES actions
/// via its own one-transaction-per-day idempotency, never a per-action flag (the processed_on column
/// was dropped by D94/M5). VERSIONED like bars (D76): the same <c>(security_id, type, effective_date)</c> re-fetched with a
/// changed value (a restatement) appends a NEW version — never an UPDATE/DELETE — so a correction is
/// preserved and a replay pinned to an old watermark never sees it. An unchanged re-fetch is a no-op
/// (idempotent), so re-running a backfill never spawns phantom versions. Dividend cash is the UNADJUSTED
/// per-share amount stored as decimal→TEXT (D69); split ratio is REAL.
/// </summary>
public interface ICorporateActionIngestion
{
    /// <summary>Write dividend actions (type='dividend', ex_date = effective_date = event date).
    /// Returns the number of new version rows written.</summary>
    int IngestDividends(long securityId, IReadOnlyList<DividendEvent> dividends, string observedAt, string source = "eodhd");

    /// <summary>Write split actions (type='split', effective_date = event date, ratio = new/old).
    /// Returns the number of new version rows written.</summary>
    int IngestSplits(long securityId, IReadOnlyList<SplitEvent> splits, string observedAt, string source = "eodhd");
}

/// <summary>Reads corporate actions at a point in time (D76 read rule): for each
/// <c>(type, effective_date)</c>, the latest version whose <c>observed_at ≤ watermark</c>. A run (or
/// replay) pinned to an old watermark reproduces byte-identically because later-observed actions and
/// correction versions are invisible to it — the NFR1 determinism property D40 buys for bars, now for
/// the feed the ledger prices on.</summary>
public interface ICorporateActionReadService
{
    /// <summary>The as-of corporate actions for a security at <paramref name="watermark"/>, one row per
    /// (type, effective_date) — its latest visible version — ordered by effective_date then type.</summary>
    IReadOnlyList<CorporateActionRow> GetActionsAsOf(long securityId, string watermark);
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

            inserted += AppendIfNew(new CorporateActionRow
            {
                SecurityId = securityId,
                Type = "dividend",
                ExDate = d.Date,          // ex-date = event date (INTEGRATIONS §1)
                EffectiveDate = d.Date,
                // Actual cash per share (D69 decimal→TEXT). Fail CLOSED (rule 10) on a null unadjusted
                // amount rather than writing the split-adjusted value: the EODHD parse boundary already
                // rejects this, so this defends the non-provider path (a directly-constructed event).
                CashPerShare = d.UnadjustedValue ?? throw new InvalidOperationException(
                    $"Dividend security_id={securityId} ex-date {d.Date}: UnadjustedValue is null - " +
                    "refusing to write split-adjusted cash in its place (fail closed)."),
                ObservedAt = observedAt,
                Source = source
            });
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

            inserted += AppendIfNew(new CorporateActionRow
            {
                SecurityId = securityId,
                Type = "split",
                ExDate = null,            // splits carry no ex-date in the feed
                EffectiveDate = s.Date,
                Ratio = s.Ratio,         // new/old (REAL)
                ObservedAt = observedAt,
                Source = source
            });
        }
        db.SaveChanges();
        return inserted;
    }

    /// <summary>
    /// Versioned append mirroring <see cref="BarIngestionService.IngestEod"/>: read the latest version
    /// for the candidate's <c>(security_id, type, effective_date)</c> identity; if none, insert version 1;
    /// if the value differs (a restatement), append version+1; if identical, no-op (idempotent). The
    /// caller sets every field except <c>Version</c>. Returns 1 if a row was written, else 0.
    /// </summary>
    private int AppendIfNew(CorporateActionRow candidate)
    {
        var versions = db.CorporateActions
            .Where(c => c.SecurityId == candidate.SecurityId
                        && c.Type == candidate.Type
                        && c.EffectiveDate == candidate.EffectiveDate)
            .ToList();

        if (versions.Count == 0)
        {
            candidate.Version = 1;
            db.CorporateActions.Add(candidate);
            return 1;
        }
        var latest = versions.OrderByDescending(c => c.Version).First();
        if (Differs(latest, candidate))
        {
            // No-backdate guard (mirrors BarIngestionService.IngestEod): a differing observation whose
            // observed_at is older than what the store already knows must not append a new top version —
            // it would shadow the newer restatement for every read at a later watermark. A resumed
            // replay re-staging its frozen vintage is the writer this protects against; its own reads
            // resolve by watermark, so the skip is lossless.
            var maxObservedAt = versions.Select(c => c.ObservedAt).OrderBy(s => s, StringComparer.Ordinal).Last();
            if (string.CompareOrdinal(candidate.ObservedAt, maxObservedAt) < 0) return 0;

            // A restatement — append the next version. Never mutate the prior one.
            candidate.Version = latest.Version + 1;
            db.CorporateActions.Add(candidate);
            return 1;
        }
        return 0; // identical re-fetch ⇒ idempotent no-op.
    }

    // The value fields that make an action a DIFFERENT observation of the same identity. observed_at /
    // source are provenance, not value, so they never trigger a version.
    private static bool Differs(CorporateActionRow existing, CorporateActionRow incoming) =>
        existing.ExDate != incoming.ExDate
        || existing.CashPerShare != incoming.CashPerShare
        || existing.Ratio != incoming.Ratio
        || existing.NewSymbol != incoming.NewSymbol
        || existing.CounterpartySecurityId != incoming.CounterpartySecurityId;
}

public sealed class CorporateActionReadService(AlphaLabDbContext db) : ICorporateActionReadService
{
    public IReadOnlyList<CorporateActionRow> GetActionsAsOf(long securityId, string watermark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);
        // Filter the security in SQL (its action count is tiny — ~85 for AAPL — so no date-range
        // pushdown is needed); resolve the visible version in memory, so the watermark comparison is a
        // plain ordinal string compare (ISO-8601 sorts chronologically), mirroring BarReadService.
        return db.CorporateActions
            .Where(c => c.SecurityId == securityId)
            .AsEnumerable()
            .Where(c => string.CompareOrdinal(c.ObservedAt, watermark) <= 0)
            .GroupBy(c => new { c.Type, c.EffectiveDate })
            .Select(g => g.OrderByDescending(c => c.Version).First())
            .OrderBy(c => c.EffectiveDate)
            .ThenBy(c => c.Type)
            .ToList();
    }
}
