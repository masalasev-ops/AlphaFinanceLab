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
        if (double.IsNaN(observedGapAnn) || double.IsInfinity(mdeAnn)) return PromotionVerdict.TooEarly;
        if (Math.Abs(observedGapAnn) < mdeAnn) return PromotionVerdict.TooEarly;  // inside the MDE (rule 6)
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
