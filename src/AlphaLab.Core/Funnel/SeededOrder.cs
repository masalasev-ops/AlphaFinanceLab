using AlphaLab.Core.Domain;
using AlphaLab.Core.Numerics;

namespace AlphaLab.Core.Funnel;

/// <summary>
/// The Stage-3 SEEDED STABLE ORDER (STRATEGY_CATALOG §3): the rule that settles which of two
/// equally-scored names is preferred at a selection boundary.
///
/// **The catalog's requirement, verbatim:** *"equal scores at a selection boundary — routine for binary
/// scorers (§6.4 Breakout, §6.6 in sign mode) and for any capped mode — break by a seeded stable order
/// derived from (`Config.Seed`, `security_id`), never by ingestion order, ticker sort, or
/// dictionary/hash order."* Until 6.3 the funnel broke ties by ascending <c>security_id</c>, which IS
/// ingestion order — one of the three the rule names — and it did so under a comment that read as though
/// determinism were the whole requirement. Determinism was necessary and not sufficient.
///
/// **Why an arbitrary-but-fixed order is not good enough.** A tie-break is only a boundary detail when
/// ties are rare. They are not: a binary scorer assigns 1.0 to every qualifying name, so the tie-break
/// becomes the ENTIRE selection rule whenever the qualifying count exceeds the cap. Under ascending id
/// that means a strategy holds the lowest-numbered names — i.e. the earliest-ingested ones — every day,
/// forever, and its book is a fact about the security master rather than about the signal. A seeded
/// permutation makes the choice among equals arbitrary in the way the strategy INTENDED it to be, and
/// two siblings differing only in <c>Config.Seed</c> get genuinely different draws instead of the
/// identical one.
///
/// **Totality is by construction, not by luck.** Callers sort by <see cref="KeyFor"/> and then by
/// <c>security_id</c>. A 64-bit collision has never been observed on a universe of hundreds, but the
/// order must be total for the wish list to be reproducible at all — so the id remains the final,
/// always-distinct tiebreak beneath the hash rather than a probabilistic hope.
///
/// PURE — no DB, no clock, no I/O; the same (seed, id) pair yields the same key on every machine.
/// </summary>
public static class SeededOrder
{
    /// <summary>
    /// The stable ordering key for <paramref name="id"/> under <paramref name="seed"/>.
    ///
    /// Composed exactly like <c>PopulationRng.Score</c>'s key chain — seed folded in first, then the
    /// security — because that construction is already load-bearing for FX-PopDeterminism and copying a
    /// proven shape is cheaper to trust than inventing a second one.
    /// </summary>
    public static ulong KeyFor(int seed, SecurityId id)
    {
        var h = Mix.SplitMix64(unchecked((ulong)(uint)seed + Mix.Gamma));
        return Mix.SplitMix64(h ^ unchecked((ulong)id.Value));
    }
}
