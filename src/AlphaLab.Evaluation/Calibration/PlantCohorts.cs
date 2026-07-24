using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Populations;

namespace AlphaLab.Evaluation.Calibration;

/// <summary>One plant to seed: a fresh population member (at a DEDICATED member index outside the real
/// population's [0, Size) range — same family breadth/sizing/cost model by construction) plus the D64
/// overlay. The strategy id round-trips every parameter so the equity step needs no side table.</summary>
public sealed record PlantSpec(
    string StrategyId, PlantKind Kind, string Family, double AlphaAnnPct, int Seed, int MemberIndex, int HorizonDays)
{
    public long Key => PlantOverlay.PlantKey(Kind, Family, AlphaAnnPct, Seed);
}

/// <summary>
/// The Phase-4 plant cohorts (FR-36 / MASTER §20.9 / v1.9.23; Change 4 per-cadence ladder): no-edge +
/// anti-predictive + naive on the DAILY family, a single DAILY edge plant at the primary level (a
/// SURVIVAL case for the finding-113 floor — daily cannot PROMOTE under its cost drag, so it is never swept
/// or made the primary), and the MONTHLY edge LADDER (Calibration.Plant.MonthlyEdgeLadderPct, default
/// geometric 2/4/8/16) — which both carries the promotable cohort AND is the C-1 detection-power sweep, its
/// per-rung promotion the checkpoint's primary finding. Seeds per cohort = Calibration.Plant.SeedsPerPlant.
/// </summary>
public static class PlantCohorts
{
    public const string IdPrefix = "plant:";

    /// <summary>Plant member indexes start here — far outside any real population's [0, Size).</summary>
    public const int MemberIndexBase = 10_000;

    /// <summary>Per-cohort index stride (seeds within a cohort occupy base + stride·cohort + seed).</summary>
    private const int CohortStride = 1_000;

    public static IReadOnlyList<PlantSpec> Build(PlantOptions plant, IReadOnlyList<PopulationFamily> families)
    {
        ArgumentNullException.ThrowIfNull(plant);
        var daily = families.First(f => f is { Name: "daily", CostsOn: true });
        var monthly = families.FirstOrDefault(f => f is { Name: "monthly", CostsOn: true });

        var specs = new List<PlantSpec>();
        var cohort = 0;
        void Add(PlantKind kind, PopulationFamily family, double alphaAnnPct)
        {
            for (var seed = 0; seed < plant.SeedsPerPlant; seed++)
            {
                specs.Add(new PlantSpec(
                    Id(kind, family.Name, alphaAnnPct, seed), kind, family.Name, alphaAnnPct, seed,
                    MemberIndexBase + cohort * CohortStride + seed, family.RedrawIntervalDays));
            }
            cohort++;
        }

        Add(PlantKind.Edge, daily, plant.AlphaAnnualPct);   // daily SURVIVAL plant only — FloorEdge, never Primary/swept
        Add(PlantKind.NoEdge, daily, 0.0);
        Add(PlantKind.Anti, daily, plant.AntiAlphaAnnualPct);
        Add(PlantKind.Naive, daily, plant.AlphaAnnualPct);
        // The monthly ladder: the promotable cohort + the C-1 detection-power sweep, per-cadence (Change 4).
        // Deduped so a rung equal to AlphaAnnualPct still yields ONE monthly edge cohort at that level.
        if (monthly is not null)
        {
            foreach (var rung in plant.MonthlyEdgeLadderPct.Distinct())
            {
                Add(PlantKind.Edge, monthly, rung);
            }
        }
        return specs;
    }

    /// <summary>`plant:{kind}:{family}:{alpha}:{seed}` — e.g. `plant:edge:daily:2:0`.</summary>
    public static string Id(PlantKind kind, string family, double alphaAnnPct, int seed) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{IdPrefix}{Token(kind)}:{family}:{alphaAnnPct:0.##}:{seed}");

    public static bool IsPlantId(string strategyId) => strategyId.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>Parse a plant strategy id back to its spec (fail closed on a malformed id).</summary>
    public static PlantSpec Parse(string strategyId, IReadOnlyList<PlantSpec> specs) =>
        specs.FirstOrDefault(s => s.StrategyId == strategyId)
        ?? throw new InvalidOperationException($"'{strategyId}' is not a seeded plant of this cohort set (fail closed).");

    private static string Token(PlantKind kind) => kind switch
    {
        PlantKind.Edge => "edge",
        PlantKind.NoEdge => "noedge",
        PlantKind.Anti => "anti",
        PlantKind.Naive => "naive",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
