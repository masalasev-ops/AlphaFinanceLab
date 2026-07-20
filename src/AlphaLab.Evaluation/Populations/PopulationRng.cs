namespace AlphaLab.Evaluation.Populations;

/// <summary>
/// The deterministic per-member score source (STRATEGY_CATALOG §5.2). A member's score for a security
/// on a given re-draw date derives ONLY from (familySeed, memberIndex, dateOrdinal, securityId) via a
/// stable hash (SplitMix64 mixing) — never a per-day reseed, never a clock, never <see cref="Random"/>.
/// This is a HARD requirement (FX-PopDeterminism): the same seeds + watermark must reproduce identical
/// member trades and equity, run after run and machine after machine.
///
/// Order-independence: because the score is a pure hash of the four keys (not a stream advanced in
/// security-iteration order), the top-N selection is identical regardless of how the eligible list is
/// enumerated — the one property a stateful RNG stream could silently break.
/// </summary>
public static class PopulationRng
{
    /// <summary>A uniform score in [0,1) for (familySeed, memberIndex, dateOrdinal, securityId).</summary>
    public static double Score(int familySeed, int memberIndex, long dateOrdinal, long securityId)
    {
        var h = SplitMix64(unchecked((ulong)(uint)familySeed + 0x9E3779B97F4A7C15UL));
        h = SplitMix64(h ^ unchecked((ulong)(uint)memberIndex));
        h = SplitMix64(h ^ unchecked((ulong)dateOrdinal));
        h = SplitMix64(h ^ unchecked((ulong)securityId));
        // Top 53 bits → a double in [0,1) with full mantissa resolution.
        return (h >> 11) * (1.0 / (1UL << 53));
    }

    private static ulong SplitMix64(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }
}
