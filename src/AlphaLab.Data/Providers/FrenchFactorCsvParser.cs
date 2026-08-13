using System.Globalization;

namespace AlphaLab.Data.Providers;

/// <summary>One parsed observation, already in the storage convention: ISO date, canonical factor
/// token, and a DECIMAL return (not percent).</summary>
public sealed record FactorObservation(string Date, string Factor, double Value);

/// <summary>Thrown when a French file does not have the shape the parser requires. Fail loud: a
/// silently-empty parse would write nothing and look like a quiet no-op refresh.</summary>
public sealed class FrenchFactorFormatException(string message) : Exception(message);

/// <summary>
/// Parses a Ken French Data Library daily CSV (INTEGRATIONS §3). Pure — the fetch, the unzip and the
/// latin1 decode all happen before this, so every format rule is testable without a network.
///
/// **THE SHAPE, and why each rule is a rule.** The published file is:
/// <code>
///   This file was created by CMPT_ME_BEME_RETS using the 202607 CRSP database. …   ← prose preamble
///                                                                                  ← blank line
///   ,Mkt-RF,SMB,HML,RMW,CMA,RF                                                      ← header, EMPTY first cell
///   19630701,-0.67, 0.02,-0.35, 0.03, 0.13,0.012                                    ← YYYYMMDD rows
///   …
///                                                                                   ← blank line
///   Annual Factors: January-December                                                ← a SECOND table
/// </code>
/// The preamble is prose of unspecified length, so the header is FOUND rather than counted — the first
/// row whose leading cell is empty and whose remaining cells contain a factor this parser knows. Data
/// then runs while the leading cell is exactly eight digits, and STOPS at the first row that is not:
/// that single rule ends the daily block at the blank line, and it is also what keeps the annual table
/// out. Reading the annual rows as daily ones would silently store 60 wildly-wrong observations dated
/// like years.
///
/// **PERCENT → DECIMAL HAPPENS HERE, ONCE.** The file publishes percent (`-0.67` is −0.67 %). Storing
/// that verbatim would leave every consumer one ÷100 away from correct, and the symptom of forgetting —
/// a β off by 100× — is not obviously a units bug. The boundary converts.
///
/// **MISSING IS DROPPED, NOT STORED AS A NUMBER.** French writes `-99.99` / `-999` for "no data". Those
/// are sentinels, not returns; storing them would put a −99 % day into a regression. Dropped, so the
/// date is simply absent and the continuity check is what notices.
/// </summary>
public static class FrenchFactorCsvParser
{
    /// <summary>Published column name → the SCHEMA token. `Mom` becomes `UMD`, which is the one rename:
    /// SCHEMA's comment enumerates `UMD`, the library's momentum file calls the column `Mom`.</summary>
    private static readonly Dictionary<string, string> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mkt-RF"] = "MKT_RF",
        ["SMB"] = "SMB",
        ["HML"] = "HML",
        ["RMW"] = "RMW",
        ["CMA"] = "CMA",
        ["RF"] = "RF",
        ["Mom"] = "UMD",
    };

    /// <summary>French's no-data sentinels. Compared with a tolerance because they arrive as text.</summary>
    private static bool IsMissing(double v) =>
        Math.Abs(v - (-99.99)) < 1e-9 || Math.Abs(v - (-999.0)) < 1e-9;

    public static IReadOnlyList<FactorObservation> Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var rows = DelimitedCsvReader.Parse(csv);

        // ---- find the header ----
        var headerIndex = -1;
        Dictionary<int, string>? columns = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Length < 2 || row[0].Trim().Length != 0) continue;

            var found = new Dictionary<int, string>();
            for (var c = 1; c < row.Length; c++)
            {
                if (Canonical.TryGetValue(row[c].Trim(), out var token)) found[c] = token;
            }
            if (found.Count == 0) continue;

            headerIndex = i;
            columns = found;
            break;
        }

        if (columns is null)
        {
            throw new FrenchFactorFormatException(
                "No factor header row found: expected a row whose first cell is empty and whose remaining " +
                $"cells name at least one of {string.Join(", ", Canonical.Keys)}. The file's layout changed, " +
                "or the payload is not a French factor CSV (an HTML error page decodes without throwing).");
        }

        // ---- the daily block: rows whose leading cell is exactly eight digits, until one is not ----
        var observations = new List<FactorObservation>();
        var sawAnyDataRow = false;

        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var key = row[0].Trim();

            if (!IsEightDigits(key))
            {
                // The daily block has ended. Everything after is the annual table or trailing prose,
                // and reading it as daily data is the defect this stop-rule exists to prevent.
                if (sawAnyDataRow) break;
                continue;   // still inside the gap between the header and the first data row
            }

            sawAnyDataRow = true;
            var date = $"{key[..4]}-{key.Substring(4, 2)}-{key.Substring(6, 2)}";

            foreach (var (col, token) in columns)
            {
                if (col >= row.Length) continue;
                var raw = row[col].Trim();
                if (raw.Length == 0) continue;

                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    throw new FrenchFactorFormatException(
                        $"Unparseable value '{raw}' for {token} on {date}. Refusing the whole file rather " +
                        "than skipping the cell: a value that does not parse means the layout is not what " +
                        "this parser thinks it is, and the remaining columns cannot be trusted either.");
                }

                if (IsMissing(percent)) continue;
                observations.Add(new FactorObservation(date, token, percent / 100.0));
            }
        }

        if (!sawAnyDataRow)
        {
            throw new FrenchFactorFormatException(
                "A factor header was found but no YYYYMMDD data row followed it. Refusing rather than " +
                "reporting a successful refresh of zero rows.");
        }

        return observations;
    }

    private static bool IsEightDigits(string s)
    {
        if (s.Length != 8) return false;
        foreach (var ch in s)
        {
            if (ch is < '0' or > '9') return false;
        }
        return true;
    }
}
