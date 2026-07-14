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
}
