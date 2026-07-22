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
/// The Phase-4 plant cohorts (FR-36 / MASTER §20.9 / v1.9.23): edge + no-edge + anti-predictive on the
/// DAILY family, the low-turnover edge cohort on the MONTHLY family (the survival-floor calibration must
/// include it), the naive constant-drift comparator (sensitivity check only), and the C-1
/// detection-power sweep (edge at ~2×/4× the primary level — with the primary cohort, three alpha
/// levels on the same seed count). Seeds per cohort = Calibration.Plant.SeedsPerPlant.
/// </summary>
public static class PlantCohorts
{
    public const string IdPrefix = "plant:";

    /// <summary>Plant member indexes start here — far outside any real population's [0, Size).</summary>
    public const int MemberIndexBase = 10_000;

    /// <summary>Per-cohort index stride (seeds within a cohort occupy base + stride·cohort + seed).</summary>
    private const int CohortStride = 1_000;

    /// <summary>The C-1 sweep multipliers on the primary edge level (2% → 4% and 8% at the default).</summary>
    private static readonly double[] SweepMultipliers = [2.0, 4.0];

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

        Add(PlantKind.Edge, daily, plant.AlphaAnnualPct);
        Add(PlantKind.NoEdge, daily, 0.0);
        Add(PlantKind.Anti, daily, plant.AntiAlphaAnnualPct);
        Add(PlantKind.Naive, daily, plant.AlphaAnnualPct);
        if (monthly is not null) Add(PlantKind.Edge, monthly, plant.AlphaAnnualPct);
        foreach (var multiplier in SweepMultipliers)
        {
            Add(PlantKind.Edge, daily, plant.AlphaAnnualPct * multiplier);
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
