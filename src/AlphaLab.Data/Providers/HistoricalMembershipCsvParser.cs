namespace AlphaLab.Data.Providers;

/// <summary>One historical membership snapshot: the full index roster as of <see cref="Date"/>.
/// <see cref="RawTickers"/> are the source spellings (dot form, `*Q` suffixes) — canonicalization to
/// EODHD symbols happens during ingestion.</summary>
public sealed record HistoricalMembershipSnapshot(string Date, IReadOnlyList<string> RawTickers);

/// <summary>
/// Parser for the fja05680/sp500 community CSV (INTEGRATIONS §8) — the S&amp;P 500 historical
/// components file used to reconstruct as-of membership (D49/D70). Header <c>date,tickers</c>; one
/// row per date; the roster is a SINGLE quoted comma-separated field, so the RFC-4180 reader keeps
/// it as one field and we split the inner list on commas. Snapshots are returned sorted by date
/// (ISO-8601 sorts chronologically). Symbology quirks (dot `BRK.B`, bankruptcy `*Q`) are preserved
/// here and normalized at ingestion — never stripped (so legitimate Q-tickers like HPQ/CPQ survive).
/// </summary>
public static class HistoricalMembershipCsvParser
{
    public static IReadOnlyList<HistoricalMembershipSnapshot> Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var rows = DelimitedCsvReader.Parse(csv);
        var snapshots = new List<HistoricalMembershipSnapshot>();
        foreach (var row in rows)
        {
            if (row.Length < 2) continue;              // blank / short line
            var date = row[0].Trim();
            if (date.Length == 0 || date == "date") continue; // header or empty date
            var tickers = row[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tickers.Length == 0) continue;
            snapshots.Add(new HistoricalMembershipSnapshot(date, tickers));
        }
        snapshots.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
        return snapshots;
    }
}
