using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>
/// Writes versioned append-only bars (FR-2 / D40). A correction inserts a NEW
/// <c>(security_id, date, version, observed_at)</c> row — never an UPDATE/DELETE (rule 3). An
/// unchanged re-fetch is a no-op (idempotent), so re-running a backfill does not spawn phantom
/// versions. <paramref name="observedAt"/> is when WE saw this data — the point-in-time key.
/// </summary>
public interface IBarIngestionService
{
    /// <summary>Ingest bars for one security. Returns the number of new version rows written.</summary>
    int IngestEod(long securityId, IReadOnlyList<EodBar> bars, string observedAt, string source = "eodhd");
}

/// <summary>Reads bars at a point in time (FR-2 read rule): the latest version whose
/// <c>observed_at ≤ watermark</c>. A run pinned to an old watermark reproduces byte-identically
/// because later-observed correction versions are invisible to it.</summary>
public interface IBarReadService
{
    /// <summary>The as-of bar for (security, date) at <paramref name="watermark"/>, or null if every
    /// version of it was observed after the watermark (or none exists).</summary>
    BarRow? GetBar(long securityId, string date, string watermark);

    /// <summary>The as-of series over [from, to] (inclusive) at <paramref name="watermark"/>,
    /// ordered by date — one row per date (its latest visible version).</summary>
    IReadOnlyList<BarRow> GetSeries(long securityId, string from, string to, string watermark);
}

public sealed class BarIngestionService(AlphaLabDbContext db) : IBarIngestionService
{
    public int IngestEod(long securityId, IReadOnlyList<EodBar> bars, string observedAt, string source = "eodhd")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedAt);
        var inserted = 0;

        foreach (var b in bars)
        {
            // Latest existing version for this (security, date), if any.
            var latest = db.Bars
                .Where(x => x.SecurityId == securityId && x.Date == b.Date)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();

            if (latest is null)
            {
                db.Bars.Add(ToRow(securityId, b, version: 1, observedAt, source));
                inserted++;
            }
            else if (Differs(latest, b))
            {
                // A correction — append the next version. Never mutate the prior one.
                db.Bars.Add(ToRow(securityId, b, version: latest.Version + 1, observedAt, source));
                inserted++;
            }
            // else: identical re-fetch ⇒ idempotent no-op.
        }

        db.SaveChanges();
        return inserted;
    }

    private static BarRow ToRow(long securityId, EodBar b, int version, string observedAt, string source) => new()
    {
        SecurityId = securityId,
        Date = b.Date,
        Version = version,
        ObservedAt = observedAt,
        Open = b.Open,
        High = b.High,
        Low = b.Low,
        Close = b.Close,
        Volume = b.Volume,
        AdjClose = b.AdjClose, // adj_open/adj_high/adj_low stay NULL (EODHD supplies no adjusted OHL)
        Source = source
    };

    private static bool Differs(BarRow existing, EodBar incoming) =>
        existing.Open != incoming.Open
        || existing.High != incoming.High
        || existing.Low != incoming.Low
        || existing.Close != incoming.Close
        || existing.Volume != incoming.Volume
        || existing.AdjClose != incoming.AdjClose;
}

public sealed class BarReadService(AlphaLabDbContext db) : IBarReadService
{
    public BarRow? GetBar(long securityId, string date, string watermark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);
        // Filter (security, date) in SQL; resolve latest-visible version in memory so the watermark
        // comparison is a plain ordinal string compare (ISO-8601 sorts chronologically).
        return db.Bars
            .Where(x => x.SecurityId == securityId && x.Date == date)
            .AsEnumerable()
            .Where(x => string.CompareOrdinal(x.ObservedAt, watermark) <= 0)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();
    }

    public IReadOnlyList<BarRow> GetSeries(long securityId, string from, string to, string watermark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);
        // Push the [from, to] range into SQL so PK (security_id, date, version) serves it, instead of
        // materializing the security's whole 20-year history to return a window. string.Compare(..) >= 0
        // / <= 0 is EF's translatable form (SecurityMaster.ResolveAsOf warns CompareOrdinal may not
        // translate); SQLite's default BINARY collation is ordinal, so on ISO-8601 dates lexical order ==
        // chronological. The watermark compare stays in memory (GetBar precedent) once the range narrows.
        return db.Bars
            .Where(x => x.SecurityId == securityId
                        && string.Compare(x.Date, from) >= 0
                        && string.Compare(x.Date, to) <= 0)
            .AsEnumerable()
            .Where(x => string.CompareOrdinal(x.ObservedAt, watermark) <= 0)
            .GroupBy(x => x.Date)
            .Select(g => g.OrderByDescending(x => x.Version).First())
            .OrderBy(x => x.Date)
            .ToList();
    }
}
