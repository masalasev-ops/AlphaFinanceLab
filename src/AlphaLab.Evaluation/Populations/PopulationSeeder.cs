using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Json;
using AlphaLab.Data;
using AlphaLab.Data.Entities;

namespace AlphaLab.Evaluation.Populations;

/// <summary>The matched-params payload persisted to control_populations.matched_params_json — the shape
/// the population is the null for (N, sizing, ExitPolicy shape, cost model) plus a realized-turnover
/// target for re-matching (finding 115; null until the turnover step fills it).</summary>
public sealed record MatchedParams(
    int N, string Sizing, string ExitShape, string CostModelVersion, int RedrawIntervalDays, double? TurnoverTargetAnnual);

/// <summary>
/// Registers the control_populations rows (idempotent) — the definitions the <see cref="PopulationEngine"/>
/// simulates. Idempotency key is (family, costs_on, family_seed): a re-seed reuses the existing row rather
/// than minting a duplicate. Writes via the caller's transaction (the LedgerStore precedent — the Worker's
/// Stage-2 transaction owns the commit; D59 sole writer).
/// </summary>
public sealed class PopulationSeeder(AlphaLabDbContext db, PopulationsOptions options)
{
    /// <summary>Ensure a control_populations row exists for each Phase-3 family and return the
    /// (family → population_id) map. Idempotent.</summary>
    public IReadOnlyDictionary<PopulationFamily, long> Seed(string costModelVersion, int selectionN = PopulationFamilies.DefaultSelectionN)
    {
        var families = PopulationFamilies.ForPhase3(options, selectionN);
        var map = new Dictionary<PopulationFamily, long>(families.Count);

        foreach (var f in families)
        {
            var costsOn = f.CostsOn;
            var existing = db.ControlPopulations.FirstOrDefault(
                p => p.Family == f.Name && p.CostsOn == costsOn && p.FamilySeed == f.FamilySeed);

            if (existing is null)
            {
                var matched = new MatchedParams(
                    f.SelectionN, "equal", ExitShapeFor(f), costModelVersion, f.RedrawIntervalDays, TurnoverTargetAnnual: null);
                existing = new ControlPopulationRow
                {
                    Family = f.Name,
                    FamilySeed = f.FamilySeed,
                    M = f.Size,
                    CostsOn = costsOn,
                    MatchedParamsJson = JsonSerializer.Serialize(matched, AlphaLabJson.Options),
                };
                db.ControlPopulations.Add(existing);
                db.SaveChanges();
            }
            else if (existing.M != f.Size)
            {
                // Keep M in step with the configured Size. If Size is raised mid-arena the daily compute
                // now brings the new members in at their own inception (DailyPipeline.ComputePopulations),
                // so control_populations.M must reflect the true member count rather than the stale one it
                // was first seeded with — otherwise M disagrees with the persisted control_equity rows.
                existing.M = f.Size;
                db.SaveChanges();
            }

            map[f] = existing.PopulationId;
        }

        return map;
    }

    // Phase-3 populations mirror the family's ExitPolicy SHAPE; cadence encodes the exit (a re-draw drops
    // any name that leaves the top-N). Recorded for provenance / re-matching, not behaviour.
    private static string ExitShapeFor(PopulationFamily f) => f.RedrawIntervalDays switch
    {
        1 => "redraw_daily",
        PopulationFamilies.MonthlyInterval => "redraw_monthly",
        _ => "redraw_banded",
    };
}
