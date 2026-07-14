using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FR-6 Alpaca bar cross-check is a DORMANT seam at launch (no Alpaca account; the Secrets Alpaca
/// pair is optional, NFR-4). Like the other dormant providers it fails loud rather than returning an
/// empty set that would read as "cross-check agreed". Activation is a config/wiring change once the
/// optional keys exist — the gate logic does not change.
/// </summary>
public class AlpacaBarCrossCheckTests
{
    [Fact]
    public void DormantAlpacaBarCrossCheck_FailsLoud()
    {
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = new AlpacaBarCrossCheck().GetDailyBarsAsync("AAPL", "2026-01-01", "2026-01-31");
        });
    }
}
