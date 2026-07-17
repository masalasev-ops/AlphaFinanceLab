using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Funnel;

/// <summary>One sized target: how much money this name should represent. Notional, not shares —
/// shares need a price, and the price belongs to Stage 6 (decide at close T, fill at open T+1).
/// Money is decimal (D69), never double.</summary>
public sealed record TargetPosition(SecurityId Id, decimal TargetNotional);

/// <summary>
/// The Stage-5 outcome. <see cref="UninvestedCash"/> is derived, and it is the evidence for "no
/// padding": on a sparse day it is large, and that is the correct answer rather than a defect.
/// </summary>
public sealed record SizingResult(
    decimal Equity,
    IReadOnlyList<TargetPosition> Targets,
    IReadOnlyList<Exclusion> Excluded)
{
    /// <summary>Equity not allocated to any target — cash. A legitimate position, not a shortfall.</summary>
    public decimal UninvestedCash => Equity - Targets.Sum(t => t.TargetNotional);
}

/// <summary>
/// Stage 5 of the daily funnel (MASTER §6) — "size &amp; safety". PURE: no DB, no prices, no clock.
///
/// FR-11 IS PARTIAL IN PHASE 2 (CHANGELOG finding 169). Only <see cref="SizingMode.Equal"/> is
/// executable. InverseVol needs D42's Ledoit–Wolf covariance and Kelly needs a per-strategy
/// calibration map — both Phase 6 — so both are REFUSED here rather than quietly falling back to
/// equal weight. A silent fallback would size every position by a rule the config says it isn't
/// using: the run would look healthy, the numbers would be wrong, and nothing downstream could
/// detect it. Rule 10 says fail closed; this is what that means for a config value.
///
/// SAFETY in Phase 2 is exactly one guardrail — Sizing.PositionCapPct. The rest of the exposure
/// system (heat, regime halts, cooldown, the drawdown breaker) is FR-17 in Phase 7 and is not
/// applied here. PROGRESS records that line so a guardrail that is merely unbuilt is never
/// mistaken for one that is broken.
/// </summary>
public static class Sizing
{
    /// <summary>
    /// Size <paramref name="targets"/> against <paramref name="equity"/>.
    ///
    /// Equal weight: each name gets equity / n, then each is clamped to
    /// <see cref="SizingOptions.PositionCapPct"/> of equity. THE CLAMP LEAVES CASH — it does not
    /// redistribute the excess across the other names, because redistributing would push those names
    /// back over the same cap; the cap would then bind on the redistribution, and so on. "Cap and
    /// hold cash" is the only fixed point that respects the cap, and holding cash because the book is
    /// concentrated is the honest outcome.
    /// </summary>
    public static SizingResult Size(
        IReadOnlyList<SecurityId> targets,
        decimal equity,
        SizingMode mode,
        SizingOptions options)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);

        if (mode != SizingMode.Equal)
        {
            // Named owners, not a vague "later": whoever hits this needs to know what is missing.
            throw new NotSupportedException(
                $"Sizing.Mode = {mode} is not executable in this build. FR-11 is partial in Phase 2: only " +
                $"'{SizingMode.Equal}' is implemented. '{SizingMode.InverseVol}' needs the Ledoit–Wolf covariance " +
                $"estimator (D42, Phase 6) and '{SizingMode.Kelly}' needs a per-strategy calibration map (Phase 6+). " +
                "Refusing rather than falling back to equal weight, which would size every position by a rule the " +
                "config does not claim (rule 10).");
        }

        if (options.PositionCapPct <= 0.0 || !double.IsFinite(options.PositionCapPct))
        {
            // Not a data condition — a config that can never produce a portfolio. An all-cash lab that
            // looks like it is running is exactly the silent failure this is loud about.
            throw new ArgumentOutOfRangeException(
                nameof(options), options.PositionCapPct,
                "Sizing.PositionCapPct must be a positive fraction of equity; at or below zero no position could " +
                "ever be sized and the lab would hold cash forever while appearing to run.");
        }

        var distinct = targets.Distinct().OrderBy(x => x.Value).ToList(); // F-DET: stable order
        var excluded = new List<Exclusion>();

        if (equity <= 0m)
        {
            // Fail closed (rule 10): no equity, no orders — and say so per name rather than returning
            // a bare empty list that reads like "the strategy wanted nothing today".
            foreach (var id in distinct)
            {
                excluded.Add(new Exclusion(id, $"account equity is {equity} — not positive, so nothing can be sized."));
            }
            return new SizingResult(equity, [], excluded);
        }

        if (distinct.Count == 0) return new SizingResult(equity, [], excluded);

        var equalShare = equity / distinct.Count;
        var cap = equity * (decimal)options.PositionCapPct;
        var perName = Math.Min(equalShare, cap);

        var sized = distinct.Select(id => new TargetPosition(id, perName)).ToList();
        return new SizingResult(equity, sized, excluded);
    }
}
