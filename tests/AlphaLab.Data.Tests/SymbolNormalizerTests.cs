using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The symbol normalizer canonicalizes the three source dialects to the EODHD dash form (decision #2).
/// The no-separator holdings forms (BRKB/BFB) can't be split mechanically, so they go through a curated
/// alias map; the dot forms (historical / Wikipedia) are mechanical dot→dash.
/// </summary>
public class SymbolNormalizerTests
{
    [Theory]
    [InlineData("BRK.B", "BRK-B")] // dot form (historical / Wikipedia)
    [InlineData("BRKB", "BRK-B")]  // no-separator holdings form (curated alias)
    [InlineData("BFB", "BF-B")]
    [InlineData("BF.B", "BF-B")]
    [InlineData("AAPL", "AAPL")]   // plain ticker — no-op
    [InlineData("GOOGL", "GOOGL")] // four letters but not a class share — no-op
    [InlineData("BRK-B", "BRK-B")] // already EODHD form — idempotent
    public void ToEodhd_CanonicalizesAllThreeDialects(string raw, string expected)
    {
        Assert.Equal(expected, SymbolNormalizer.ToEodhd(raw));
    }
}
