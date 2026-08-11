using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Data.Services;

/// <summary>
/// The share-unit conversions in force for ONE session: for a given security, the factors a corporate
/// action applied to its share count in this session's <c>(previousSession, asOf]</c> window (D142).
///
/// RESOLVED OVER THE ORDER SET, NOT THE BOOK — and that is the whole reason this is a separate service
/// rather than a read off <see cref="CorporateActionOutcome"/>. The applier iterates the securities the
/// account HOLDS, so a pending BUY into a name not yet held is invisible to it. A reverse split on that
/// fill date would then buy the ordered count at the restated price and spend <c>1/r</c> of the intended
/// notional — silently, with no guard to catch it, and in breach of D84's cash sizing at the fill. A
/// ratio is a property of the SECURITY and the window; only a termination is a property of the book.
///
/// Day-scoped and MEMOISED: every account with a pending order in the same name asks the same question,
/// and one resolution per security per day is enough.
///
/// REPLAY INHERITS THE DATE CEILING FOR FREE, because this takes
/// <see cref="ICorporateActionReadService"/> by interface and the replay graph decorates it with
/// <c>DateCeilingCorporateActionReads</c>. A 2016 split stays invisible to a 2015 simulated day with no
/// second code path to keep in step.
/// </summary>
public sealed class ShareUnitRestatements(
    ICorporateActionReadService actions,
    string asOf,
    string watermark,
    string? previousSession)
{
    private static readonly IReadOnlyList<double> None = [];

    private readonly Dictionary<long, IReadOnlyList<double>> _cache = [];

    /// <summary>
    /// The factors applied to <paramref name="securityId"/>'s share count today, in APPLICATION ORDER
    /// (the same order the ledger restated the book in — see <see cref="OrderRestatement.Restate"/> on
    /// why the fold order is load-bearing). Empty when nothing restated it, which is the normal case.
    /// </summary>
    public IReadOnlyList<double> RatiosFor(SecurityId securityId)
    {
        if (_cache.TryGetValue(securityId.Value, out var cached)) return cached;

        List<double>? ratios = null;
        foreach (var row in actions.GetActionsAsOf(securityId.Value, watermark))
        {
            var action = LedgerMapping.ToDomain(row);
            if (!CorporateActionWindow.Contains(action.AppliedOn, previousSession, asOf)) continue;

            // Keyed off the ledger's own answer, never off a local list of type tokens: a future action
            // kind that restates units inherits this at compile time rather than being silently skipped.
            if (CorporateActionLedger.UnitRestatementRatio(action) is not { } ratio) continue;

            (ratios ??= []).Add(ratio);
        }

        IReadOnlyList<double> result = ratios ?? None;
        _cache[securityId.Value] = result;
        return result;
    }
}
