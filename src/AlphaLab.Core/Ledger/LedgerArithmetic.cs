namespace AlphaLab.Core.Ledger;

/// <summary>
/// The identifier of the arithmetic that turns a day's decisions into stored trades, positions and
/// equity — and therefore of the rules every number derived from a replay generation was produced under.
///
/// WHY A VERSION EXISTS AT ALL. A replay generation is ~5,000 committed sessions that took hours to
/// produce and cannot be reconstructed from anything else in the store. `ReplayRunner` SKIPS
/// same-watermark committed days, which is what makes a run resumable — and also means that after a rule
/// changes, an ordinary re-run leaves the old sessions untouched while every new one uses the new rule.
/// The result is ONE generation containing TWO arithmetics, which is what D95's one-generation-per-arena
/// rule exists to prevent, and it arrives by typing the ordinary command. `--reset` — the safe path —
/// is the opt-in. This constant is what lets the pre-flight tell the two apart.
///
/// WHAT COUNTS AS A CHANGE. Bump this when a decision changes the VALUES a day writes to `trades`,
/// `positions`, `cash_events` or `equity_curve` given identical inputs. Do NOT bump it for a change that
/// only affects reporting, read-models, monitor thresholds or anything recomputed from stored rows —
/// those are re-derivable and a mixed generation of them is not a mixed generation of evidence.
///
/// HISTORY, kept here because the constant is meaningless without it:
///  • <c>la-1</c> — everything through Phase 6 checkpoint 6.5. sp500 generation 2 (watermark
///    2026-07-24T22:00:00Z) was produced under this, BEFORE the stamp existed, so its rows carry no
///    marker at all. An unstamped generation is treated as "older than the current arithmetic and not
///    identifiable" — refused rather than assumed compatible, because assuming is the failure this
///    guards.
///  • <c>la-2</c> — D142 (a corporate action restates its security's pending orders; a sale exceeding
///    the restated holding is refused) and D143 (an event that ENDS a line cancels that security's
///    pending orders). Both change the quantity a fill books on a corporate-action date, so both change
///    stored trades and every curve derived from them.
///  • <c>la-3</c> — D147 (a frozen position is not sized on a whole-book rebalance, and a buy no longer
///    clears the freeze flag). A frozen name previously entered the rebalance set and TRADED, so the
///    trades a rebalance day books differ under this rule. The first bump driven by a decision that is
///    not about corporate actions, and exactly the case D144's rule was written for: the VALUES a day
///    writes change given identical inputs.
/// </summary>
public static class LedgerArithmetic
{
    /// <summary>The arithmetic this build produces. Compared against a generation's stamp.</summary>
    public const string Version = "la-3";

    /// <summary>
    /// The config key a generation is stamped with when it is created. Append-only-versioned like every
    /// other config row (rule 24 / D72): a change INSERTs (key, version+1) and the current value is
    /// MAX(version).
    /// </summary>
    public const string VintageConfigKey = "Replay.LedgerArithmeticVersion";
}
