using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Funnel;

/// <summary>The Stage-3 outcome: the wish list (ordered best-first), and every scored name that did
/// not make it, with the reason. The wish list is a list of names the strategy WANTS to hold — it is
/// not a list of orders, and falling off it is never itself a sell (§6 / rule 7).</summary>
public sealed record SelectionResult(
    IReadOnlyList<SecurityId> WishList,
    IReadOnlyList<Exclusion> Excluded);

/// <summary>
/// Stage 3 of the daily funnel (MASTER §6, catalog §3) — SHARED code for every strategy. Turns
/// Stage 2's scores into a wish list.
///
/// THE INVARIANT THIS TYPE EXISTS TO PROTECT: a name with score == 0 (or below a floor) is NEVER
/// selectable. Sparse days mean a SHORT wish list and more cash. No padding, ever.
///
/// Why that matters enough to be a hard rule (rule 7): the tempting bug is to always return N names
/// so the book stays fully invested — take the top 40 by score even when only 2 score above zero.
/// That silently converts "the strategy found nothing today" into "the strategy is confident in 40
/// names", which is a fabricated signal. The resulting track record would measure the universe's
/// drift, not the strategy's edge, and no downstream statistic could recover the difference. Cash is
/// a legitimate position; a padded wish list is a lie about what the model said.
///
/// PURE — no DB, no clock, no I/O.
/// </summary>
public static class Selection
{
    /// <summary>
    /// Apply the strategy's <paramref name="rule"/> and the system floor to <paramref name="scores"/>.
    ///
    /// Three filters compose, and the first cannot be loosened by any config:
    ///   1. score &gt; 0, finite — the zero-score invariant (rule 7 / §6's "score>0 only"). A NaN
    ///      score fails this too, because every comparison against NaN is false: an unscoreable name
    ///      is not a selectable name.
    ///   2. score ≥ <see cref="SelectionRule.MinScore"/> — the strategy's own floor (catalog §3
    ///      states the invariant for both modes, not just Threshold).
    ///   3. score ≥ <paramref name="guardrails"/>.MinScore — the system floor beneath it.
    ///
    /// Then the breadth cap: N under TopN, MaxConcurrent under Threshold, and in both cases
    /// Guardrails.MaxConcurrentPositions on top. Caps only ever SHORTEN the list.
    /// </summary>
    public static SelectionResult Select(
        IReadOnlyDictionary<SecurityId, double> scores,
        SelectionRule rule,
        GuardrailsOptions guardrails)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(guardrails);

        var excluded = new List<Exclusion>();
        var passing = new List<(SecurityId Id, double Score)>();

        // Deterministic iteration (F-DET): a dictionary has no guaranteed order, so order by id first.
        foreach (var (id, score) in scores.OrderBy(kv => kv.Key.Value))
        {
            // The zero-score invariant. NaN lands here by construction — `NaN > 0` is false.
            if (!(score > 0.0))
            {
                excluded.Add(new Exclusion(id, $"score {Fmt(score)} is not > 0 — never selectable (rule 7)."));
                continue;
            }
            if (score < rule.MinScore)
            {
                excluded.Add(new Exclusion(id, $"score {Fmt(score)} is below the strategy floor {Fmt(rule.MinScore)}."));
                continue;
            }
            if (score < guardrails.MinScore)
            {
                excluded.Add(new Exclusion(id, $"score {Fmt(score)} is below the system floor Guardrails.MinScore {Fmt(guardrails.MinScore)}."));
                continue;
            }
            passing.Add((id, score));
        }

        // Best-first, ties broken by security_id so the wish list is byte-identical across runs
        // (F-DET). Without the tiebreak, two names on the same score could swap places between runs
        // and — at the N boundary — silently swap which one gets bought.
        var ranked = passing
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Id.Value)
            .ToList();

        var cap = rule.Mode switch
        {
            SelectionMode.TopN => rule.N,
            SelectionMode.Threshold => rule.MaxConcurrent,
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Mode, "Unmapped selection mode."),
        };
        cap = Math.Min(cap, guardrails.MaxConcurrentPositions);

        // NOTE the direction: the cap TRUNCATES a long list; it never extends a short one. If two
        // names pass and the cap is 40, the wish list is two names long and the rest is cash.
        foreach (var (id, score) in ranked.Skip(Math.Max(cap, 0)))
        {
            excluded.Add(new Exclusion(id, $"score {Fmt(score)} passed the floors but fell outside the breadth cap of {cap}."));
        }

        var wishList = ranked.Take(Math.Max(cap, 0)).Select(x => x.Id).ToList();
        return new SelectionResult(wishList, excluded);
    }

    private static string Fmt(double v) => v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
}
