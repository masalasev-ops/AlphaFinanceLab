using System.Text.Json.Serialization;

namespace AlphaLab.Core.Domain;

/// <summary>
/// How long a strategy intends to hold (catalog §2). The calibration target P(up over horizon)
/// and any Kelly payoff estimate `b` are defined over this; Stage 4 consults
/// <see cref="ExitPolicy"/> for the actual closes — the horizon is the INTENT, the exit policy
/// is the MECHANISM, and they are deliberately separate.
///
/// Persisted to strategies.holding_horizon_days (SCHEMA), which is nullable precisely because
/// two of the three shapes have no fixed day count.
///
/// Closed hierarchy: the private constructor means no type outside this file can extend it, so
/// a `switch` over the shapes is exhaustive and a new shape cannot appear without the compiler
/// pointing at every consumer.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Days), "days")]
[JsonDerivedType(typeof(ToRankExit), "to_rank_exit")]
[JsonDerivedType(typeof(ToNextRebalance), "to_next_rebalance")]
public abstract record HoldingHorizon
{
    private HoldingHorizon() { }

    /// <summary>The day count for strategies.holding_horizon_days, or null where the horizon is
    /// not expressible in days (SCHEMA makes that column nullable for exactly this reason).</summary>
    public abstract int? Days_ { get; }

    /// <summary>A fixed intended hold of <paramref name="Count"/> sessions.</summary>
    public sealed record Days(int Count) : HoldingHorizon
    {
        public override int? Days_ => Count;
    }

    /// <summary>Hold until the rank-buffer exit fires (momentum's shape).</summary>
    public sealed record ToRankExit : HoldingHorizon
    {
        public override int? Days_ => null;
    }

    /// <summary>Hold to the next scheduled rebalance (low-vol / buy-and-hold's shape).</summary>
    public sealed record ToNextRebalance : HoldingHorizon
    {
        public override int? Days_ => null;
    }
}
