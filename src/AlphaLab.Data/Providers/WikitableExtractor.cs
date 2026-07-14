using System.Net;
using System.Text.RegularExpressions;

namespace AlphaLab.Data.Providers;

/// <summary>
/// A minimal scoped extractor for a Wikipedia constituents wikitable (no HTML package — decision #2).
/// NOT a general HTML parser: it locates the <c>id="constituents"</c> table, verifies the first
/// column header is "Symbol" (fail loud on drift — the C-4-style guard for this feed), and returns
/// each data row's first-cell text (the ticker, dot form). It reads the cell's inner text with tags
/// and comments stripped, so it works for the S&amp;P 500 page (tickers wrapped in <c>&lt;a&gt;</c>
/// links via the NyseSymbol/NasdaqSymbol templates) and the S&amp;P 100 page (plain-text cells) alike.
/// </summary>
public static class WikitableExtractor
{
    private static readonly Regex TagsAndComments =
        new(@"<!--.*?-->|<[^>]*>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Extract the constituents table's per-row first-cell tickers (raw, dot form). Throws
    /// <see cref="FormatException"/> on any markup drift the caller must not silently absorb.</summary>
    public static IReadOnlyList<string> ExtractConstituentsTable(string html)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);

        var table = SliceConstituentsTable(html);
        var rows = SplitRows(table);
        if (rows.Count == 0)
        {
            throw new FormatException("Wikipedia constituents table has no <tr> rows (markup drift).");
        }

        // Header guard (C-4 style): the first row's first cell must be the "Symbol" column.
        var header = FirstCellText(rows[0]);
        if (!string.Equals(header, "Symbol", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(
                $"Wikipedia constituents header drift: expected first column 'Symbol', got '{header ?? "<none>"}'.");
        }

        var tickers = new List<string>();
        for (var i = 1; i < rows.Count; i++)
        {
            var ticker = FirstCellText(rows[i]);
            if (!string.IsNullOrWhiteSpace(ticker)) tickers.Add(ticker!);
        }
        if (tickers.Count == 0)
        {
            throw new FormatException("Wikipedia constituents table yielded no tickers (markup drift).");
        }
        return tickers;
    }

    private static string SliceConstituentsTable(string html)
    {
        var idIdx = html.IndexOf("id=\"constituents\"", StringComparison.Ordinal);
        if (idIdx < 0)
        {
            throw new FormatException("Wikipedia constituents table not found (id=\"constituents\" missing).");
        }
        var tableStart = html.LastIndexOf("<table", idIdx, StringComparison.OrdinalIgnoreCase);
        if (tableStart < 0)
        {
            throw new FormatException("Wikipedia constituents <table> start not found before id=\"constituents\".");
        }
        var tableEnd = html.IndexOf("</table", tableStart, StringComparison.OrdinalIgnoreCase);
        if (tableEnd < 0)
        {
            throw new FormatException("Wikipedia constituents </table> not found.");
        }
        return html.Substring(tableStart, tableEnd - tableStart);
    }

    private static List<string> SplitRows(string table)
    {
        var rows = new List<string>();
        var idx = 0;
        while (true)
        {
            var trStart = table.IndexOf("<tr", idx, StringComparison.OrdinalIgnoreCase);
            if (trStart < 0) break;
            var trEnd = table.IndexOf("</tr", trStart, StringComparison.OrdinalIgnoreCase);
            if (trEnd < 0) break;
            rows.Add(table.Substring(trStart, trEnd - trStart));
            idx = trEnd + 4;
        }
        return rows;
    }

    /// <summary>Inner text of a row's first cell — whichever of &lt;td&gt;/&lt;th&gt; opens first —
    /// with tags/comments stripped and entities decoded. Null if the row has no cell.</summary>
    private static string? FirstCellText(string row)
    {
        var tdIdx = row.IndexOf("<td", StringComparison.OrdinalIgnoreCase);
        var thIdx = row.IndexOf("<th", StringComparison.OrdinalIgnoreCase);

        int start;
        string closeTag;
        if (tdIdx < 0 && thIdx < 0) return null;
        if (thIdx >= 0 && (tdIdx < 0 || thIdx < tdIdx)) { start = thIdx; closeTag = "</th"; }
        else { start = tdIdx; closeTag = "</td"; }

        var gt = row.IndexOf('>', start);
        if (gt < 0) return null;
        var end = row.IndexOf(closeTag, gt, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;

        var inner = row.Substring(gt + 1, end - gt - 1);
        var text = TagsAndComments.Replace(inner, string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }
}
