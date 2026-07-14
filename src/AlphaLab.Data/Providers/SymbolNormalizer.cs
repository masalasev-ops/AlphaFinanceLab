namespace AlphaLab.Data.Providers;

/// <summary>
/// Canonicalizes a source ticker to the EODHD symbol form (decision #2 — EODHD is the identity
/// anchor, so bars join on the same spelling). The three membership sources spell share classes
/// three ways: the IVV/OEF holdings CSV uses NO separator (BRKB), the fja05680 historical CSV and
/// Wikipedia use a dot (BRK.B), and EODHD uses a dash (BRK-B). Dot→dash is mechanical; the
/// no-separator holdings forms CANNOT be split mechanically (BRKB could be BRK-B or a genuine
/// four-letter ticker), so a small curated alias map covers the S&amp;P class shares that actually
/// appear in the fixtures. If a live backfill surfaces a new no-separator class share, extend the
/// map — stop and report, never guess a split point.
/// </summary>
public static class SymbolNormalizer
{
    private static readonly IReadOnlyDictionary<string, string> HoldingsAlias =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BRKB"] = "BRK-B", // Berkshire Hathaway class B
            ["BFB"] = "BF-B",   // Brown-Forman class B
        };

    /// <summary>Return the EODHD-form symbol for a raw source ticker. Idempotent on symbols already
    /// in EODHD form (a plain ticker or an existing dash form is returned unchanged).</summary>
    public static string ToEodhd(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        var s = raw.Trim().ToUpperInvariant();
        if (HoldingsAlias.TryGetValue(s, out var mapped)) return mapped;
        return s.Replace('.', '-'); // dot form (historical / Wikipedia) → dash; no-op otherwise
    }
}
