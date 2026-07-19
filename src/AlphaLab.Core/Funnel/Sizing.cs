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
    decimal SpendableCash,
    IReadOnlyList<TargetPosition> Targets,
    IReadOnlyList<FunnelNote> Excluded)
{
    /// <summary>The spendable budget NOT deployed to any target — the cash left after sizing. A
    /// legitimate position, not a shortfall. <see cref="SpendableCash"/> is the account's available
    /// cash on an opens-only day (D84) and its equity on a whole-book rebalance (the book re-weights
    /// against itself), so this figure is honest in both cases.</summary>
    public decimal UninvestedCash => SpendableCash - Targets.Sum(t => t.TargetNotional);
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
/// SAFETY in Phase 2 is two structural constraints: Sizing.PositionCapPct (the per-name cap) and the
/// D84 CASH CONSTRAINT — new opens are sized against AVAILABLE CASH, never total equity, and scaled to
/// fit, so an account can only spend cash it holds (cash can never go negative) and no held position is
/// ever sold to fund a new open (rule 7). The rest of the exposure system (heat, regime halts, cooldown,
/// the drawdown breaker) is FR-17 in Phase 7 and is not applied here. PROGRESS records that line so a
/// guardrail that is merely unbuilt is never mistaken for one that is broken.
/// </summary>
public static class Sizing
{
    /// <summary>
    /// Size <paramref name="targets"/> to equal weight under two structural constraints.
    ///
    /// PER-NAME CAP: each name gets equity / n, then each is clamped to
    /// <see cref="SizingOptions.PositionCapPct"/> of equity. THE CLAMP LEAVES CASH — it does not
    /// redistribute the excess across the other names, because redistributing would push those names
    /// back over the same cap; the cap would then bind on the redistribution, and so on. "Cap and
    /// hold cash" is the only fixed point that respects the cap, and holding cash because the book is
    /// concentrated is the honest outcome.
    ///
    /// CASH CONSTRAINT (D84): the total sized notional can never exceed <paramref name="availableCash"/>,
    /// the budget the caller may deploy. When the capped equal-weight total would exceed it, every target
    /// is scaled DOWN proportionally so the total equals available cash (the account opens smaller); when
    /// there is no cash to deploy, nothing is opened (a sparse day, not a failure). The caller passes the
    /// account's CASH for new opens and its EQUITY for a whole-book rebalance (which re-weights the book
    /// against itself) — see <see cref="FunnelRunner"/>. This binds IN ADDITION to the per-name cap: a
    /// target is min(equal share, cap), then scaled to fit cash. No held position is ever sold to fund a
    /// new open (rule 7); cash frees up only as exits fire on their own schedule.
    /// </summary>
    public static SizingResult Size(
        IReadOnlyList<SecurityId> targets,
        decimal equity,
        decimal availableCash,
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
        var excluded = new List<FunnelNote>();

        if (equity <= 0m)
        {
            // Fail closed (rule 10): no equity, no orders — and say so per name rather than returning
            // a bare empty list that reads like "the strategy wanted nothing today".
            foreach (var id in distinct)
            {
                excluded.Add(new FunnelNote(id, $"account equity is {equity} — not positive, so nothing can be sized."));
            }
            return new SizingResult(equity, availableCash, [], excluded);
        }

        if (distinct.Count == 0) return new SizingResult(equity, availableCash, [], excluded);

        // CASH CONSTRAINT (D84): with no cash to deploy, open nothing. A near-zero-cash day is the
        // correct sparse outcome — the account waits for an exit to free cash rather than spending money
        // it does not have (implicit leverage) or selling a held name to fund a buy (rule 7 — only the
        // ExitPolicy closes).
        if (availableCash <= 0m)
        {
            foreach (var id in distinct)
            {
                excluded.Add(new FunnelNote(id,
                    $"no cash available to open ({availableCash}); the open is deferred until an exit or corporate " +
                    "action frees cash (D84). Held positions are untouched (rule 7)."));
            }
            return new SizingResult(equity, availableCash, [], excluded);
        }

        var equalShare = equity / distinct.Count;
        var cap = equity * (decimal)options.PositionCapPct;
        var perName = Math.Min(equalShare, cap);

        // Bind the cash constraint on top of the per-name cap: if the capped equal-weight book would
        // spend more than the account holds, scale every target down proportionally so the total is
        // exactly available cash (equal weights ⇒ this equals perName = availableCash / n).
        var totalIntended = perName * distinct.Count;
        if (totalIntended > availableCash)
        {
            perName *= availableCash / totalIntended;
        }

        var sized = distinct.Select(id => new TargetPosition(id, perName)).ToList();
        return new SizingResult(equity, availableCash, sized, excluded);
    }
}
