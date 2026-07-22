using AlphaLab.Core.Config;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Populations;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// FX-PlantOverlay (FR-36 / D64, MASTER §20.9): the pure overlay process — persistent two-state
/// activity at the configured stationary fraction, per-active-day drift netting the annualized target,
/// regime renormalization, determinism, and the never-constant-drift guarantee that separates the
/// calibration plant from the prohibited naive comparator.
/// </summary>
public class PlantOverlayTests
{
    private const double Frac = 0.25;
    private const int Horizon = 21;
    private static readonly long EdgeKey = PlantOverlay.PlantKey(PlantKind.Edge, "daily", 2.0, 0);

    private static double MeanRun => PlantOverlay.MeanActiveRun(0.9, Horizon);

    [Fact]
    public void FX_PlantOverlay_StationaryActiveFraction()
    {
        const int n = 100_000;
        var active = 0;
        for (var t = 0; t < n; t++)
        {
            if (PlantOverlay.IsActive(EdgeKey, t, Frac, MeanRun)) active++;
        }
        Assert.InRange(active / (double)n, 0.22, 0.28);   // 0.25 ± sampling noise at run-length scale
    }

    [Fact]
    public void FX_PlantOverlay_UnconditionalTargetNetted()
    {
        const int n = 100_000;
        var sum = 0.0;
        for (var t = 0; t < n; t++)
        {
            sum += PlantOverlay.OverlayReturn(PlantKind.Edge, 2.0, EdgeKey, t, Frac, MeanRun, 1.0, 1.0);
        }
        var annualizedPct = sum / n * 252 * 100;
        Assert.InRange(annualizedPct, 1.7, 2.3);          // nets to ~2%/yr (D64 AlphaAnnualPct)

        // The anti plant is the exact mirror shape at its own key: negative, same magnitude scale.
        var antiKey = PlantOverlay.PlantKey(PlantKind.Anti, "daily", -2.0, 0);
        var antiSum = 0.0;
        for (var t = 0; t < n; t++)
        {
            var o = PlantOverlay.OverlayReturn(PlantKind.Anti, -2.0, antiKey, t, Frac, MeanRun, 1.0, 1.0);
            Assert.True(o <= 0);
            antiSum += o;
        }
        Assert.InRange(antiSum / n * 252 * 100, -2.3, -1.7);
    }

    [Fact]
    public void FX_PlantOverlay_MeanRunLengthScalesToHorizon()
    {
        // φ=0.9 floors the mean run at 10 sessions; a longer family horizon scales it up (D64).
        Assert.Equal(10.0, PlantOverlay.MeanActiveRun(0.9, 1), 6);
        Assert.Equal(21.0, PlantOverlay.MeanActiveRun(0.9, 21), 6);

        // Measure the realized mean ACTIVE run length over a long path — ≈ the configured mean.
        const int n = 200_000;
        var runs = new List<int>();
        var current = 0;
        for (var t = 0; t < n; t++)
        {
            if (PlantOverlay.IsActive(EdgeKey, t, Frac, MeanRun)) current++;
            else if (current > 0) { runs.Add(current); current = 0; }
        }
        Assert.InRange(runs.Average(), MeanRun * 0.75, MeanRun * 1.25);
    }

    [Fact]
    public void FX_PlantOverlay_Deterministic_OrderIndependent()
    {
        var sequential = Enumerable.Range(0, 2_000).Select(t => PlantOverlay.IsActive(EdgeKey, t, Frac, MeanRun)).ToArray();
        // Query the SAME ordinals in a scrambled order — activity is a pure function of (key, t).
        foreach (var t in Enumerable.Range(0, 2_000).OrderBy(t => (t * 977) % 2_000))
        {
            Assert.Equal(sequential[t], PlantOverlay.IsActive(EdgeKey, t, Frac, MeanRun));
        }
        // And a different seed's key is a different path (not one shared path relabeled).
        var otherKey = PlantOverlay.PlantKey(PlantKind.Edge, "daily", 2.0, 1);
        var other = Enumerable.Range(0, 2_000).Select(t => PlantOverlay.IsActive(otherKey, t, Frac, MeanRun)).ToArray();
        Assert.NotEqual(sequential, other);
    }

    [Fact]
    public void FX_PlantOverlay_NeverConstantDrift()
    {
        // The REALISTIC edge overlay has both active and inactive sessions — never constant.
        var values = Enumerable.Range(0, 500)
            .Select(t => PlantOverlay.OverlayReturn(PlantKind.Edge, 2.0, EdgeKey, t, Frac, MeanRun, 1.0, 1.0))
            .ToList();
        Assert.Contains(values, v => v == 0.0);
        Assert.Contains(values, v => v > 0.0);

        // The naive comparator IS constant drift — it exists only for the sensitivity check.
        var naive = Enumerable.Range(0, 500)
            .Select(t => PlantOverlay.OverlayReturn(PlantKind.Naive, 2.0, EdgeKey, t, Frac, MeanRun, 1.0, 1.0))
            .Distinct()
            .ToList();
        Assert.Single(naive);
        Assert.Equal(2.0 / 100 / 252, naive[0], 12);

        // The no-edge plant carries NO overlay at all (D63: it must sit at the band median).
        Assert.All(Enumerable.Range(0, 100),
            t => Assert.Equal(0.0, PlantOverlay.OverlayReturn(PlantKind.NoEdge, 0.0, EdgeKey, t, Frac, MeanRun, 1.0, 1.0)));
    }

    [Fact]
    public void FX_PlantOverlay_RegimeRenormalization_IsPitAndTargetPreserving()
    {
        // The running mean is a pure function of counts ≤ t — trivially leak-free (F-LEAK shape).
        Assert.Equal(1.0, PlantOverlay.RunningMultiplierMean(0, 0, 1.25, 0.5), 12);   // unlabeled ⇒ neutral
        Assert.Equal(1.25, PlantOverlay.RunningMultiplierMean(10, 0, 1.25, 0.5), 12); // all-bull ⇒ m/m̄ = 1
        Assert.Equal(0.875, PlantOverlay.RunningMultiplierMean(5, 5, 1.25, 0.5), 12); // even mix

        // In an all-bull stretch the renormalizer divides the bull multiplier away exactly, so the
        // unconditional target is preserved rather than inflated by 1.25×.
        var bullAdjusted = PlantOverlay.OverlayReturn(PlantKind.Edge, 2.0, EdgeKey, FirstActive(), Frac, MeanRun, 1.25, 1.25);
        var neutral = PlantOverlay.OverlayReturn(PlantKind.Edge, 2.0, EdgeKey, FirstActive(), Frac, MeanRun, 1.0, 1.0);
        Assert.Equal(neutral, bullAdjusted, 12);
    }

    private static int FirstActive()
    {
        for (var t = 0; t < 10_000; t++)
        {
            if (PlantOverlay.IsActive(EdgeKey, t, Frac, MeanRun)) return t;
        }
        throw new InvalidOperationException("no active session in 10k ordinals — the chain is broken");
    }

    [Fact]
    public void FR36_PlantCohorts_BuildShape()
    {
        var plant = new PlantOptions { SeedsPerPlant = 3 };
        var families = PopulationFamilies.ForPhase3(new PopulationsOptions { Size = 6, CostFreeSize = 3 });
        var specs = PlantCohorts.Build(plant, families);

        // 7 cohorts × 3 seeds: edge/noedge/anti/naive on daily + edge on monthly + the 4%/8% C-1 sweep.
        Assert.Equal(21, specs.Count);
        Assert.Equal(specs.Count, specs.Select(s => s.StrategyId).Distinct().Count());
        Assert.Equal(specs.Count, specs.Select(s => s.MemberIndex).Distinct().Count());
        Assert.All(specs, s => Assert.True(s.MemberIndex >= PlantCohorts.MemberIndexBase)); // never a real member's index
        Assert.All(specs, s => Assert.True(PlantCohorts.IsPlantId(s.StrategyId)));

        Assert.Contains(specs, s => s is { Kind: PlantKind.Edge, Family: "monthly", HorizonDays: 21 });
        Assert.Contains(specs, s => s is { Kind: PlantKind.Edge, AlphaAnnPct: 4.0 });
        Assert.Contains(specs, s => s is { Kind: PlantKind.Edge, AlphaAnnPct: 8.0 });
        Assert.Contains(specs, s => s is { Kind: PlantKind.Naive });
        Assert.Equal("plant:edge:daily:2:0", PlantCohorts.Id(PlantKind.Edge, "daily", 2.0, 0));
    }
}
