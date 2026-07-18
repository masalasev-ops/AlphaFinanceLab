using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>The per-endpoint EODHD request weights (checkpoint 2.12 / INTEGRATIONS §1). api_usage_log must
/// weight by these or it under-reports against the 100k/day cap.</summary>
public class EodhdEndpointCostTests
{
    [Theory]
    [InlineData(EodhdEndpoint.Eod, 1)]
    [InlineData(EodhdEndpoint.Div, 1)]
    [InlineData(EodhdEndpoint.Splits, 1)]
    [InlineData(EodhdEndpoint.News, 5)]
    [InlineData(EodhdEndpoint.BulkLastDay, 100)]
    public void For_ReturnsTheDocumentedWeight(EodhdEndpoint endpoint, int expected)
    {
        Assert.Equal(expected, EodhdEndpointCost.For(endpoint));
    }

    [Fact]
    public void For_UnknownEndpoint_FailsClosed()
    {
        // An undefined enum value is not silently costed at 1 (which would under-report) — it throws.
        Assert.Throws<ArgumentOutOfRangeException>(() => EodhdEndpointCost.For((EodhdEndpoint)999));
    }
}
