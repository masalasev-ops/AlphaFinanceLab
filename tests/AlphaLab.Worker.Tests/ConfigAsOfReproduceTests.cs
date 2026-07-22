using AlphaLab.Core.Config;
using AlphaLab.Data.Entities;
using AlphaLab.Worker.Ops;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// D96, the reproduce-day cousin of the visibility test (user-mandated): inserting a config row AFTER
/// a session committed must leave that session byte-identical under reproduction — the config analogue
/// of the perturbation-diverges proof. This is exactly the property the Phase-4 calibration relies on:
/// freezing the D56 curve rows must not change what any earlier committed day "read".
/// </summary>
public class ConfigAsOfReproduceTests
{
    [Fact]
    public async Task D96_ReproduceDay_ByteIdenticalAfterPostAsOfConfigInsert()
    {
        using var h = new PipelineHarness();
        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);

        // A LOAD-BEARING key, re-pointed at nonsense AFTER Run2 committed: without the as-of read, the
        // reproduction's MAX(version) would resolve the bogus proxy id, the CW account's universe would
        // change, and the day's decisions would diverge. With D96 the row is invisible at Run2's watermark.
        using (var db = h.Open())
        {
            db.Config.Add(new ConfigRow
            {
                Key = AlphaLab.Strategies.CapWeightProxy.ProxySecurityIdConfigKey,
                ValueJson = "999999",
                Version = 2,
                ChangedOn = h.Run3,          // strictly after Run2's {asOf}T22:00:00Z watermark
                Reason = "test: a post-session config append (the calibration-freeze shape)",
            });
            db.SaveChanges();
        }

        var runner = new ReproduceDayRunner(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Populations:Size"] = "6",
                ["Populations:CostFreeSize"] = "3",
            }).Build(),
            new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" },
            NullLoggerFactory.Instance);

        var outcome = await runner.RunAsync($"Data Source={h.DbPath}", h.Run2);

        Assert.True(outcome.Matches, string.Join("\n", outcome.Differences));
    }
}
