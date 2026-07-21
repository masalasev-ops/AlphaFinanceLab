using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Populations;

namespace AlphaLab.Evaluation.Tests;

public class PopulationSeederTests
{
    [Fact]
    public void Seed_IsIdempotent_AndSyncsMWhenSizeChanges()
    {
        using var arena = new EvalArena();
        var opts = new PopulationsOptions { Size = 200, CostFreeSize = 50 };

        using (var db = arena.Open())
        {
            new PopulationSeeder(db, opts).Seed("cost-v1");
            Assert.Equal(4, db.ControlPopulations.Count());   // daily/banded/monthly (cost-on) + the cost-free daily twin
            Assert.Equal(200, db.ControlPopulations.Single(p => p.Family == "daily" && p.CostsOn).M);
        }

        // Raise Size mid-arena: the seeder reuses the existing rows (idempotent on family+costs_on+seed) but
        // brings M into step with the new Size, so control_populations.M never disagrees with the member
        // count the daily compute now produces (each new member enters at its own inception).
        opts.Size = 250;
        using (var db = arena.Open())
        {
            new PopulationSeeder(db, opts).Seed("cost-v1");
            Assert.Equal(4, db.ControlPopulations.Count());   // still exactly four — no duplicates
            Assert.Equal(250, db.ControlPopulations.Single(p => p.Family == "daily" && p.CostsOn).M);
            Assert.Equal(50, db.ControlPopulations.Single(p => !p.CostsOn).M);   // the cost-free twin's Size is unchanged
        }
    }
}
