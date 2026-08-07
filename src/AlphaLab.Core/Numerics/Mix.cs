namespace AlphaLab.Core.Numerics;

/// <summary>
/// The stable 64-bit mixing primitive (SplitMix64) every deterministic-order rule in the lab derives
/// from. Moved to Core at checkpoint 6.3 so the funnel's seeded tie-break and the population's score
/// source share ONE mixer instead of two copies of the same constants.
///
/// **Why sharing matters more than the usual DRY argument.** Both callers stake a determinism claim on
/// it — <c>FX-PopDeterminism</c> ("the same seeds + watermark must reproduce identical member trades and
/// equity, run after run and machine after machine") and the Stage-3 seeded order. Two copies that drift
/// would break both claims silently and in different directions, and neither test would name the mixer
/// as the cause.
///
/// NEVER <see cref="object.GetHashCode"/>: string and object hash codes are randomized per process in
/// .NET, so an order derived from one would differ between runs of the same binary — the exact
/// non-determinism the seeded order exists to remove.
/// </summary>
public static class Mix
{
    /// <summary>The golden-ratio odd constant SplitMix64 increments by (2^64 / φ).</summary>
    public const ulong Gamma = 0x9E3779B97F4A7C15UL;

    /// <summary>One SplitMix64 round: avalanche <paramref name="x"/> so adjacent inputs land far apart.</summary>
    public static ulong SplitMix64(ulong x)
    {
        unchecked
        {
            x += Gamma;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }
}
