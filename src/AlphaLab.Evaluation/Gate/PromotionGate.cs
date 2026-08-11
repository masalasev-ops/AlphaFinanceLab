namespace AlphaLab.Evaluation.Gate;

/// <summary>The paired promotion verdict (D31/D48). TooEarly is the honest default: the gate never acts
/// on a gap smaller than the pair's current MDE, nor before the minimum track.</summary>
public enum PromotionVerdict
{
    TooEarly,
    Promoted,
    Refused,
}

/// <summary>
/// The pure paired gate. Given the observed annualized A−B gap and the pair's current NW-corrected MDE,
/// it returns Promoted / Refused / TooEarly. This is the ONE place the "inside the MDE ⇒ TooEarly" rule
/// (hard rule 6) lives; the go_live_log event + the status transition are layered on top (checkpoint 3.5).
/// </summary>
public static class PromotionGate
{
    public static PromotionVerdict Decide(double observedGapAnn, double mdeAnn, int trackDays, int minTrackDays)
    {
        if (trackDays < minTrackDays) return PromotionVerdict.TooEarly;      // not enough evidence yet

        // An MDE that is not a number is not a threshold, and a gap that is not a number is not a
        // measurement. Both mean "nothing is adjudicable here" (D146). IsNaN(mdeAnn) was the asymmetry:
        // the guard checked IsNaN on the GAP and IsInfinity on the MDE, so a NaN MDE fell through —
        // `Math.Abs(gap) <= NaN` is false and `gap > 0` then decided a verdict off an unestimable bound.
        if (double.IsNaN(observedGapAnn) || double.IsNaN(mdeAnn) || double.IsInfinity(mdeAnn))
        {
            return PromotionVerdict.TooEarly;
        }

        // Inside the MDE ⇒ TooEarly (rule 6). The comparison is `<=`, not `<`, and the boundary is the
        // whole point rather than a rounding nicety: a gap EQUAL to the smallest detectable effect is not
        // OUTSIDE it, and the degenerate pair — a strategy that has never traded, whose flat equity curve
        // yields gap 0 and MDE 0 — lands exactly there. Under `<`, `0 < 0` is false and the strategy fell
        // through to `0 > 0` ⇒ REFUSED: an absence of evidence rendered as a directional finding that it
        // is WORSE than the benchmark. That verdict then un-dimmed the alpha cell (UX-1), sorted the row
        // into below-or-flagged, and — because SeparationState treats any non-TooEarly verdict as decisive
        // — suppressed the IndistinguishableFromRandom chip for precisely the strategy most
        // indistinguishable from random (rule 21). The MDE rail exists so that "we cannot tell" never
        // renders as "we can".
        if (Math.Abs(observedGapAnn) <= mdeAnn) return PromotionVerdict.TooEarly;

        return observedGapAnn > 0 ? PromotionVerdict.Promoted : PromotionVerdict.Refused;
    }

    /// <summary>The go_live_log / power_reports verdict token (SCHEMA: Promoted|Refused|TooEarly|Revert).</summary>
    public static string ToToken(PromotionVerdict v) => v switch
    {
        PromotionVerdict.Promoted => "Promoted",
        PromotionVerdict.Refused => "Refused",
        _ => "TooEarly",
    };
}
