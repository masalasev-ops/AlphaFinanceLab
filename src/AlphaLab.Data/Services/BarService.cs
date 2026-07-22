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

    /// <summary>The as-of CROSS-SECTION for a single <paramref name="date"/> at
    /// <paramref name="watermark"/> — every security's latest visible version on that date, ordered by
    /// security_id (D78). Date-major, served by <c>ix_bars_date</c>; the Phase-2 funnel / Phase-4 replay
    /// read shape ("every name at date D at watermark W"). One row per security.</summary>
    IReadOnlyList<BarRow> GetCrossSection(string date, string watermark);

    /// <summary>The latest stored bar date ≤ <paramref name="upTo"/> with any version visible at
    /// <paramref name="watermark"/>, or null if none — the incremental-fetch cursor. A single MAX(date)
    /// query served by the (security_id, date, version) PK, never a full-series materialization
    /// (finding 193): the old GetSeries("0001-01-01", …) path loaded a security's entire history per
    /// security per day, which the sp500 widen and multi-day catch-up cannot afford.</summary>
    string? LastStoredDate(long securityId, string upTo, string watermark);
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

    public IReadOnlyList<BarRow> GetCrossSection(string date, string watermark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);
        // Push the single-date equality into SQL so ix_bars_date serves it (instead of scanning bars for
        // a date that isn't the PK's leading column); resolve the visible version per security in memory,
        // mirroring GetSeries — one row per security, its latest version whose observed_at ≤ watermark.
        return db.Bars
            .Where(x => x.Date == date)
            .AsEnumerable()
            .Where(x => string.CompareOrdinal(x.ObservedAt, watermark) <= 0)
            .GroupBy(x => x.SecurityId)
            .Select(g => g.OrderByDescending(x => x.Version).First())
            .OrderBy(x => x.SecurityId)
            .ToList();
    }

    public string? LastStoredDate(long securityId, string upTo, string watermark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upTo);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);
        // Everything stays in SQL — MAX(date) over the PK range, with both comparisons in EF's
        // translatable string.Compare form (SQLite's BINARY collation is ordinal, so on ISO-8601
        // strings lexical order == chronological; the GetSeries comment records the same reasoning).
        // The date's mere existence at the watermark is the question, so no version resolution is
        // needed: any visible version of a date proves the date is stored.
        return db.Bars
            .Where(x => x.SecurityId == securityId
                        && string.Compare(x.Date, upTo) <= 0
                        && string.Compare(x.ObservedAt, watermark) <= 0)
            .Max(x => (string?)x.Date);
    }
}
