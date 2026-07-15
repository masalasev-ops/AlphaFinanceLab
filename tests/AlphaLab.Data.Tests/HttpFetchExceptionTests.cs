using AlphaLab.Data.Http;

namespace AlphaLab.Data.Tests;

/// <summary>A fetch failure must never surface a secret carried in the URL query (EODHD ?api_token=…) —
/// hard rule 11 / D67. The message keeps the host+path for debugging but redacts the query.</summary>
public class HttpFetchExceptionTests
{
    [Fact]
    public void Message_RedactsQueryString_SoTheTokenNeverLeaks()
    {
        var ex = new HttpFetchException("https://eodhd.com/api/eod/AAPL.US?api_token=SUPERSECRET&fmt=json", new Exception("boom"));
        Assert.DoesNotContain("SUPERSECRET", ex.Message);
        Assert.Contains("https://eodhd.com/api/eod/AAPL.US", ex.Message); // path retained for diagnosis
        Assert.Contains("<redacted>", ex.Message);
    }

    [Fact]
    public void Message_WithoutQuery_IsUnchanged()
    {
        Assert.Contains("https://x/y", new HttpFetchException("https://x/y", new Exception()).Message);
    }
}
