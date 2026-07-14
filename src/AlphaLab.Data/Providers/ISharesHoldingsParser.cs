namespace AlphaLab.Data.Providers;

/// <summary>One equity holding from an iShares holdings CSV (IVV/OEF). Only Ticker + Sector are
/// consumed by membership in 1.4; Name/AssetClass ride along for context and the equity filter.</summary>
public sealed record HoldingRow(string Ticker, string? Name, string Sector, string AssetClass);

/// <summary>
/// Parser for the iShares holdings CSV (INTEGRATIONS §2/§2b) — one shape serves both IVV
/// (portfolioId 239726) and OEF (239723). The file is 8 metadata lines + a blank line, then the
/// 15-column header, data rows, a blank line, and a multi-line quoted disclaimer field. We SCAN for
/// the header (never assume a fixed skip-count), assert it verbatim (C-4: renamed/moved column ⇒
/// <see cref="FormatException"/>, never a silent empty-set "agreement"), take the contiguous
/// 15-column data rows, and keep equity holdings only (Asset Class == "Equity"). Pure/static so it
/// is unit-tested offline against the byte-real fixture.
/// </summary>
public static class ISharesHoldingsParser
{
    /// <summary>The exact 15-column header (INTEGRATIONS §2, snapshot for the C-4 guard / FX-IvvHeader).</summary>
    public const string ExpectedHeader =
        "Ticker,Name,Sector,Asset Class,Market Value,Weight (%),Notional Value,Quantity,Price,Location,Exchange,Currency,FX Rate,Market Currency,Accrual Date";

    private static readonly string[] ExpectedColumns = ExpectedHeader.Split(',');

    private const string EquityAssetClass = "Equity";

    public static IReadOnlyList<HoldingRow> ParseHoldings(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var rows = DelimitedCsvReader.Parse(csv);
        var headerIndex = FindHeaderIndex(rows); // throws on drift / not-found (C-4)

        var holdings = new List<HoldingRow>();
        for (var r = headerIndex + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            // Data is contiguous 15-column rows; the first short row is the blank line that precedes
            // the multi-line disclaimer footer — stop there rather than parse the footer.
            if (row.Length < ExpectedColumns.Length) break;

            var ticker = row[0].Trim();
            var assetClass = row[3].Trim();
            if (assetClass != EquityAssetClass) continue;         // drop cash/derivative/futures rows
            if (ticker.Length == 0 || ticker == "-") continue;    // defensive: placeholder ticker

            holdings.Add(new HoldingRow(ticker, NullIfBlank(row[1]), row[2].Trim(), assetClass));
        }
        return holdings;
    }

    private static string? NullIfBlank(string s)
    {
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// Locate the header row and enforce the C-4 guard. The header is the FIRST row with exactly the
    /// expected column count; it must equal the expected header verbatim (a renamed/moved column
    /// fails loud). A dropped/added column changes the arity so NO row matches — that also fails loud
    /// ("header not found") rather than silently returning an empty set.
    /// </summary>
    private static int FindHeaderIndex(IReadOnlyList<string[]> rows)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Length != ExpectedColumns.Length) continue;

            for (var c = 0; c < ExpectedColumns.Length; c++)
            {
                if (!string.Equals(row[c].Trim(), ExpectedColumns[c], StringComparison.Ordinal))
                {
                    throw new FormatException(
                        "iShares holdings header drift (C-4): the first 15-column row did not match the expected header.\n" +
                        $"  expected: {ExpectedHeader}\n" +
                        $"  actual:   {string.Join(",", row)}");
                }
            }
            return r;
        }

        throw new FormatException(
            "iShares holdings header not found (C-4): no row matched the expected 15-column header — " +
            "a column was likely added or removed. Never treat this as an empty membership set.");
    }
}
