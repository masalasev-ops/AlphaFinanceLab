namespace AlphaLab.Data.Tests;

/// <summary>
/// Loads real captured provider payloads copied next to the test assembly (see the csproj Content
/// item). These are byte-real EODHD responses captured 2026-07-13 (response body only — never a
/// request URL or token). Parse tests read them verbatim so the parsers are exercised against the
/// actual wire shapes, not synthetic guesses.
/// </summary>
internal static class Fixtures
{
    public static string Eodhd(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "eodhd", fileName));

    /// <summary>Byte-real iShares holdings CSV (IVV/OEF) copied next to the assembly (§2/§2b).</summary>
    public static string Holdings(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    /// <summary>Re-saved rendered Wikipedia constituents page (§7).</summary>
    public static string Wikipedia(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    /// <summary>fja05680/sp500 historical components CSV (§8).</summary>
    public static string Historical(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
