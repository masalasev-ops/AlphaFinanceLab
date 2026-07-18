using System.Text.Json.Serialization;

namespace AlphaLab.Core.Domain;

/// <summary>
/// Declarative exit rules, serialized into strategies.exit_policy_json and executed by SHARED
/// Stage-4 code (catalog §2). The five shapes below are the complete declared set.
///
/// Hard rule 7 / MASTER §6: the wish list opens and adds; ONLY the ExitPolicy closes — plus
/// forced events (corporate actions per §13.6, guardrail circuit-breakers). "Fell off today's
/// wish list" is never an implicit sell, and a universe exit (D74) is never a close.
///
/// PHASING (Phase 2): all five shapes are DECLARED so exit_policy_json round-trips every
/// strategy the catalog names, but only three are EXECUTABLE — Never, RankBuffer, and
/// ScheduledRebalance, which are the ones Phase 2's dummies need. TargetOrTimeStop and
/// ChannelExit throw from the executor naming their Phase-6 owners. That is deliberate:
/// TargetOrTimeStop's `exitCondition` is genuinely unspecified in the catalog (§2 names the
/// shape but never defines the reversion condition), and guessing a condition would silently
/// invent strategy behaviour. Refusing is the fail-closed answer (hard rule 10).
///
/// Closed hierarchy (private constructor): no shape can be added outside this file, so the
/// executor's switch is exhaustive and a sixth shape cannot slip past the compiler.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Never), "never")]
[JsonDerivedType(typeof(RankBuffer), "rank_buffer")]
[JsonDerivedType(typeof(ScheduledRebalance), "scheduled_rebalance")]
[JsonDerivedType(typeof(TargetOrTimeStop), "target_or_time_stop")]
[JsonDerivedType(typeof(ChannelExit), "channel_exit")]
public abstract record ExitPolicy
{
    private ExitPolicy() { }

    /// <summary>Buy-and-hold: never closes on signal. Only forced events can close (catalog §5.1).</summary>
    public sealed record Never : ExitPolicy;

    /// <summary>Exit when cross-sectional rank falls below <paramref name="ExitRank"/> (momentum).
    /// Rank hysteresis: enter at rank ≤ N, exit below ~2N — this is what kills boundary-churn
    /// cost bleed, so ExitRank is meant to be materially larger than the selection N.</summary>
    public sealed record RankBuffer(int ExitRank) : ExitPolicy;

    /// <summary>Hold to the next rebalance every <paramref name="EveryNDays"/> sessions
    /// (low-vol; the self-built equal-weight benchmark's monthly rebalance, D68).</summary>
    public sealed record ScheduledRebalance(int EveryNDays) : ExitPolicy;

    /// <summary>Exit on the reversion condition or the time stop (mean reversion). DECLARED ONLY —
    /// the executor refuses it in Phase 2. <paramref name="ExitCondition"/> is an opaque token the
    /// catalog never defines; Phase 6's MeanReversion owns both its grammar and its evaluation.</summary>
    public sealed record TargetOrTimeStop(string ExitCondition, int MaxHoldDays) : ExitPolicy;

    /// <summary>Exit on a close below the N-day low channel (breakout). DECLARED ONLY — the
    /// executor refuses it in Phase 2; Phase 6's Breakout owns it.</summary>
    public sealed record ChannelExit(int ExitChannel) : ExitPolicy;
}
