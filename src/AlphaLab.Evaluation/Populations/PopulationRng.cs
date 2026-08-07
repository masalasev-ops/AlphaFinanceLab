using AlphaLab.Core.Numerics;

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
        // The mixer moved to AlphaLab.Core.Numerics.Mix at 6.3 (shared with the Stage-3 seeded order);
        // the ARITHMETIC is byte-for-byte the same rounds and constants, so generation 2's member
        // scores are unchanged. FX-PopDeterminism is what proves that, and it is why the move is safe.
        var h = Mix.SplitMix64(unchecked((ulong)(uint)familySeed + Mix.Gamma));
        h = Mix.SplitMix64(h ^ unchecked((ulong)(uint)memberIndex));
        h = Mix.SplitMix64(h ^ unchecked((ulong)dateOrdinal));
        h = Mix.SplitMix64(h ^ unchecked((ulong)securityId));
        // Top 53 bits → a double in [0,1) with full mantissa resolution.
        return (h >> 11) * (1.0 / (1UL << 53));
    }
}
