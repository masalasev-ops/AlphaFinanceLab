using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests.Pipeline;

/// <summary>
/// The random control populations (D36) computed inside the D53 Stage-2 write (checkpoint 3.3). The
/// harness runs a small population (Size=6, CostFreeSize=3) so these assert structure + accumulation; the
/// determinism / gross-vs-net band properties are pinned by the pure-engine tests in AlphaLab.Evaluation.
/// </summary>
public class PopulationPipelineTests
{
    // Size=6 × 3 cost-on families (daily/banded/monthly) + CostFreeSize=3 = 21 members per day.
    private const int MembersPerDay = 6 * 3 + 3;

    [Fact]
    public async Task RunDay_SeedsFourPopulations_AndWritesControlEquityPerMember()
    {
        using var h = new PipelineHarness();

        var result = await h.RunAsync(h.Run1);
        Assert.True(result.Committed);

        using var db = h.Open();

        var pops = db.ControlPopulations.ToList();
        Assert.Equal(4, pops.Count);
        Assert.Equal(3, pops.Count(p => p.CostsOn));                 // daily/banded/monthly, costs on
        Assert.Single(pops.Where(p => !p.CostsOn));                  // the cost-free daily twin

        // The cost-free band is the daily family's costs-off twin — same family + seed, costs_on=0, M=3.
        var dailyOn = pops.Single(p => p.Family == "daily" && p.CostsOn);
        var costFree = pops.Single(p => !p.CostsOn);
        Assert.Equal("daily", costFree.Family);
        Assert.Equal(dailyOn.FamilySeed, costFree.FamilySeed);
        Assert.Equal(3, costFree.M);

        var equity = db.ControlEquity.Where(e => e.AsOf == h.Run1 && e.RunKind == "live").ToList();
        Assert.Equal(MembersPerDay, equity.Count);
        Assert.All(equity, e => Assert.True(e.Equity > 0m));         // real, positive equity
    }

    [Fact]
    public async Task RunDays_AccumulateOneControlEquityRowPerMemberPerDay_SeedingIdempotent()
    {
        using var h = new PipelineHarness();

        await h.RunAsync(h.Run1);
        await h.RunAsync(h.Run2);
        await h.RunAsync(h.Run3);

        using var db = h.Open();

        Assert.Equal(MembersPerDay, db.ControlEquity.Count(e => e.AsOf == h.Run1 && e.RunKind == "live"));
        Assert.Equal(MembersPerDay, db.ControlEquity.Count(e => e.AsOf == h.Run2 && e.RunKind == "live"));
        Assert.Equal(MembersPerDay, db.ControlEquity.Count(e => e.AsOf == h.Run3 && e.RunKind == "live"));

        // Seeding is idempotent across days — still exactly the four populations.
        Assert.Equal(4, db.ControlPopulations.Count());

        // No replay rows can appear on the forward path (rule 1 / FR-33).
        Assert.False(db.ControlEquity.Any(e => e.RunKind != "live"));
    }
}
