namespace AlphaLab.Strategies.Tests;

/// <summary>Finding I — the cap-weight benchmark proxy follows the traded universe's membership source,
/// config-driven so the S&amp;P-500 widening flips OEF→IVV with no code change; unknown sources fail closed.</summary>
public class CapWeightProxyTests
{
    [Theory]
    [InlineData("oef_csv", "OEF.US")] // S&P 100 slice (D70)
    [InlineData("ivv_csv", "IVV.US")] // after the widening
    public void SymbolFor_MapsTheKnownMembershipSources(string source, string symbol) =>
        Assert.Equal(symbol, CapWeightProxy.SymbolFor(source));

    [Fact]
    public void SymbolFor_UnknownSource_FailsClosed() =>
        Assert.Throws<NotSupportedException>(() => CapWeightProxy.SymbolFor("eodhd_something"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SymbolFor_BlankSource_Throws(string? source) =>
        Assert.ThrowsAny<ArgumentException>(() => CapWeightProxy.SymbolFor(source!));
}
