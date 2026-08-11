namespace AlphaLab.Core.Ledger;

/// <summary>
/// WHICH corporate actions a session applies: the half-open window <c>(previousSession, asOf]</c>.
///
/// ONE definition, because two consumers must agree exactly or the ledger and the orders it fills fall
/// out of step. The applier uses it to decide which actions restate the BOOK today; the fill path uses
/// it to decide which restate that security's PENDING ORDERS today (D142). If those windows differed by
/// a single session, a split would be applied to one and not the other — which is the defect D142 fixes,
/// reintroduced by a copy.
///
/// WHY HALF-OPEN, and why the left edge is exclusive. Consecutive sessions must PARTITION the date line:
/// every calendar date belongs to exactly one session's window, so an action applies exactly once. An
/// action effective on a weekend or holiday therefore lands on the next session rather than being
/// skipped (finding 192), and an action effective ON a session is applied by that session — before its
/// funnel runs, so the book the day's orders are built against is already restated.
/// </summary>
public static class CorporateActionWindow
{
    /// <summary>
    /// Does an action applied on <paramref name="appliedOn"/> fall in this session's window?
    /// </summary>
    /// <param name="appliedOn">
    /// The action's <c>AppliedOn</c> — the ex-date for a dividend, the effective date otherwise. Never
    /// the raw effective date: a dividend's ex-date is the day the position's value drops, and that is
    /// the day the cash must appear (D30).
    /// </param>
    /// <param name="previousSession">
    /// The session before <paramref name="asOf"/>, or null at the very first session, where the window
    /// is open on the left and everything up to <paramref name="asOf"/> applies.
    /// </param>
    /// <param name="asOf">The session being processed; inclusive.</param>
    public static bool Contains(string appliedOn, string? previousSession, string asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appliedOn);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        return string.CompareOrdinal(appliedOn, asOf) <= 0
               && (previousSession is null || string.CompareOrdinal(appliedOn, previousSession) > 0);
    }
}
