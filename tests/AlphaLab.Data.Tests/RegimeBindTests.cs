using AlphaLab.Data;
using AlphaLab.Data.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AlphaLab.Data.Tests;

/// <summary>
/// Finding F — the DI seam that makes <c>Regime.*</c> config stop being silently ignored. Before this,
/// <c>AddAlphaLabMembership</c> unconditionally registered an unbound <c>new RegimeOptions()</c>, so every
/// bound value was discarded. FR-26 (the regime-label service) is the first consumer, so 2.8 owns the
/// Regime bind: the composition root binds the section and passes the result, which the extension honors.
/// (The <c>DataQualityOptions</c>/<c>CalendarOptions</c> siblings bind with their own consumers later.)
/// </summary>
public class RegimeBindTests
{
    [Fact]
    public void FindingF_PassedRegimeOptions_IsTheResolvedInstance_NotASilentDefault()
    {
        // Stands in for config.GetSection("Regime").Get<RegimeOptions>() at the composition root.
        var bound = new RegimeOptions { TrendSmaDays = 123, VolPercentile = 77, ProxySecurityId = 42 };

        var services = new ServiceCollection();
        services.AddAlphaLabMembership(bound);
        using var sp = services.BuildServiceProvider();

        Assert.Same(bound, sp.GetRequiredService<RegimeOptions>());   // the bound values flow — finding F closed
        // The FR-26 service is registered (its construction needs the DbContext, so assert the descriptor).
        Assert.Contains(services, d => d.ServiceType == typeof(IRegimeLabelService));
    }

    [Fact]
    public void FindingF_NoBoundOptions_KeepsTheConfigDefaults()
    {
        var services = new ServiceCollection();
        services.AddAlphaLabMembership();          // existing callers pass nothing → defaults, unchanged
        using var sp = services.BuildServiceProvider();

        var resolved = sp.GetRequiredService<RegimeOptions>();
        Assert.Equal(200, resolved.TrendSmaDays);  // CONFIG_REFERENCE default
        Assert.Equal(3, resolved.VolLookbackYears);
    }
}
