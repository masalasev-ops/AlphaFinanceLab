using AlphaLab.Core.Config;

namespace AlphaLab.Evaluation.Calibration;

/// <summary>The three D64 plant kinds plus the naive constant-drift comparator (which exists ONLY for
/// the plant-sensitivity check — constant drift is prohibited as the calibration plant, MASTER §20.9).</summary>
public enum PlantKind
{
    Edge,
    NoEdge,
    Anti,
    Naive,
}

/// <summary>
/// The pure D64 overlay process (FR-36, MASTER §20.9): a PERSISTENT two-state activity chain with
/// stationary active fraction <c>ActiveDayFrac</c> and mean active run length max(1/(1−φ), horizon),
/// per-active-day drift scaled so the annualized overlay nets to the target, regime multipliers on the
/// PIT trend label renormalized by the RUNNING realized mix — never constant daily drift.
///
/// DETERMINISM WITHOUT SEQUENTIAL STATE. The activity sequence is a renewal chain: alternating
/// inactive/active RUNS whose lengths are geometric draws keyed by SplitMix64(plantKey, runIndex) — so
/// activity at ordinal t is a pure function of (plantKey, t), reconstructible in any order, no Random,
/// no persisted chain state. Walking runs (not days) keeps the lookup O(t / meanRunLength).
/// </summary>
public static class PlantOverlay
{
    private const int TradingDaysPerYear = 252;

    /// <summary>Is the plant's overlay ACTIVE at session <paramref name="ordinal"/> (0-based from the
    /// replay window start)? Renewal chain: runs alternate inactive→active; the mean inactive run is
    /// sized so the stationary active fraction equals <paramref name="activeDayFrac"/>.</summary>
    public static bool IsActive(long plantKey, int ordinal, double activeDayFrac, double meanActiveRun)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(activeDayFrac, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(activeDayFrac, 1.0);

        var meanInactiveRun = meanActiveRun * (1.0 - activeDayFrac) / activeDayFrac;
        var position = 0;
        var runIndex = 0;
        var active = false;                                   // the chain opens inactive
        while (true)
        {
            var length = GeometricRunLength(plantKey, runIndex, active ? meanActiveRun : meanInactiveRun);
            if (ordinal < position + length) return active;
            position += length;
            runIndex++;
            active = !active;
        }
    }

    /// <summary>The overlay return for one session. <paramref name="regimeMultiplier"/> is m(label_t)
    /// (1.0 when unlabeled); <paramref name="runningMultiplierMean"/> is the PIT mean of multipliers
    /// over labeled sessions ≤ t (1.0 when none) — dividing by it renormalizes so the unconditional
    /// annual target still nets to <paramref name="alphaAnnPct"/> (D64 "renormalized", PIT-clean).</summary>
    public static double OverlayReturn(
        PlantKind kind, double alphaAnnPct, long plantKey, int ordinal,
        double activeDayFrac, double meanActiveRun,
        double regimeMultiplier, double runningMultiplierMean)
    {
        switch (kind)
        {
            case PlantKind.NoEdge:
                return 0.0;
            case PlantKind.Naive:
                // The prohibited-as-calibration constant drift — the sensitivity check's comparator.
                return alphaAnnPct / 100.0 / TradingDaysPerYear;
            case PlantKind.Edge:
            case PlantKind.Anti:
                if (!IsActive(plantKey, ordinal, activeDayFrac, meanActiveRun)) return 0.0;
                var perActiveDay = alphaAnnPct / 100.0 / TradingDaysPerYear / activeDayFrac;
                var normalizer = runningMultiplierMean > 0 ? runningMultiplierMean : 1.0;
                return perActiveDay * (regimeMultiplier / normalizer);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown plant kind.");
        }
    }

    /// <summary>Mean active run length: the per-session persistence φ sets the floor 1/(1−φ) and the
    /// family's holding horizon scales it up (D64 "PersistencePhi scaled to the family's horizon").</summary>
    public static double MeanActiveRun(double persistencePhi, int familyHorizonDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(persistencePhi, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(persistencePhi, 1.0);
        return Math.Max(1.0 / (1.0 - persistencePhi), familyHorizonDays);
    }

    /// <summary>The PIT multiplier normalizer: the mean of m over LABELED sessions ≤ t (bull/bear
    /// counts), 1.0 when nothing is labeled yet. Pure — trivially leak-free by construction.</summary>
    public static double RunningMultiplierMean(int bullCount, int bearCount, double bullMultiplier, double bearMultiplier)
    {
        var n = bullCount + bearCount;
        return n == 0 ? 1.0 : (bullCount * bullMultiplier + bearCount * bearMultiplier) / n;
    }

    // ---- deterministic draws (the PopulationRng idea, plant-keyed) ----

    // Geometric run length with the given mean (≥ 1): L = 1 + floor(ln(1−u)/ln(1−1/mean)).
    private static int GeometricRunLength(long plantKey, int runIndex, double meanLength)
    {
        if (meanLength <= 1.0) return 1;
        var u = Uniform(Hash(unchecked((ulong)plantKey * 0x9E3779B97F4A7C15UL) ^ (ulong)runIndex));
        var length = 1 + (int)Math.Floor(Math.Log(1.0 - u) / Math.Log(1.0 - 1.0 / meanLength));
        return Math.Max(1, Math.Min(length, 10_000)); // a sane ceiling; the draw's tail is unbounded
    }

    /// <summary>A stable key for one plant: hash of (kind, family, alpha level, seed) — order-free.</summary>
    public static long PlantKey(PlantKind kind, string family, double alphaAnnPct, int seed)
    {
        var h = Hash((ulong)kind + 1);
        foreach (var ch in family) h = Hash(h ^ ch);
        h = Hash(h ^ (ulong)BitConverter.DoubleToInt64Bits(alphaAnnPct));
        h = Hash(h ^ (ulong)seed);
        return unchecked((long)h);
    }

    // SplitMix64 finalizer (the PopulationRng mixing function; restated here because PopulationRng's
    // key shape is (familySeed, member, grid, security) — a different domain).
    private static ulong Hash(ulong z)
    {
        unchecked
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private static double Uniform(ulong h) => ((h >> 11) + 0.5) / 9007199254740992.0; // 53-bit mantissa → (0,1)
}
