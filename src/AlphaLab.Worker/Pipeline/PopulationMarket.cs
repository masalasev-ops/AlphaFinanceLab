using System.Globalization;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Populations;

namespace AlphaLab.Worker.Pipeline;

/// <summary>
/// The <see cref="IPopulationMarket"/> the Worker backs with the day's point-in-time <see cref="BarFeatureView"/>
/// (leak-proof, hard rule 4), the index membership, and the D43 <see cref="CostModel"/>. Built per run-day
/// inside Stage 2 and shared by all members of all families — the §5.2 "one shared read per family per day"
/// seam (returns memoized in the feature view; the eligible list memoized here).
/// </summary>
public sealed class PopulationMarket(
    BarFeatureView features,
    IIndexMembershipRead membership,
    ICalendarService calendar,
    CostModel costModel,
    int advWindowSessions) : IPopulationMarket
{
    // A fixed epoch well before any seeded arena calendar. The absolute ordinal is irrelevant — only that it
    // is monotone per session and stable across runs (the calendar is fixed), which keeps the population's
    // re-draw grid reconstructible. Arena-scoped (D71): ordinals never cross arenas.
    private static readonly DateOnly SessionEpoch = new(1990, 1, 1);

    private readonly Dictionary<string, IReadOnlyList<long>> _eligible = [];
    private readonly Dictionary<string, long> _sessionOrdinal = [];

    public IReadOnlyList<long> Eligible(string date)
    {
        if (_eligible.TryGetValue(date, out var cached)) return cached;
        var members = membership.MembersAsOf(date).ToList();
        _eligible[date] = members;
        return members;
    }

    public long SessionOrdinal(string date)
    {
        if (_sessionOrdinal.TryGetValue(date, out var cached)) return cached;
        // The trading-session index = the count of sessions from the fixed epoch through the date. The
        // calendar is binary-searched over its loaded snapshot, so this is cheap; memoized per date, and in
        // the daily compute only two distinct dates (today + prior session) are ever queried across all members.
        var ordinal = calendar.SessionsBetween(SessionEpoch, ParseDate(date)).Count - 1L;
        _sessionOrdinal[date] = ordinal;
        return ordinal;
    }

    public double DailyReturn(long securityId, string date)
    {
        var d = ParseDate(date);
        var prev = calendar.PreviousSession(d);
        if (prev is null) return 0.0;

        var id = new SecurityId(securityId);
        var today = features.AdjClose(id, d);
        var yesterday = features.AdjClose(id, prev.Value);
        if (today is not { } t || yesterday is not { } y || y <= 0.0) return 0.0;   // no bar ⇒ no return (frozen/halted)
        return t / y - 1.0;
    }

    public double OneWayCostFraction(long securityId, string date, decimal perNameNotional)
    {
        var id = new SecurityId(securityId);

        // Spread fraction (always available via the ADV-notional bucket; a null ADV falls to the widest bucket).
        var advNotional = features.Adv21Notional(id) ?? 0.0;
        var spreadFraction = costModel.HalfSpreadBp(costModel.Bucket(advNotional)) / 10_000.0;

        // Impact fraction k·σ·√(Q/ADVshares) — negligible at paper notional but the same D43 model. Falls
        // to spread-only when the share ADV / σ window is incomplete (thin history), never fabricated.
        var d = ParseDate(date);
        var priceDate = d > features.AsOf ? features.AsOf : d;
        var advShares = features.Adv21Shares(id);
        var sigma = features.RealizedVolDaily(id, advWindowSessions);
        var rawClose = features.RawClose(id, priceDate);
        var impact = 0.0;
        if (advShares is { } adv && adv > 0 && sigma is { } s && rawClose is { } px && px > 0)
        {
            var shares = (double)perNameNotional / px;
            impact = costModel.ImpactFraction(shares, adv, s);
        }

        return spreadFraction + impact;
    }

    private static DateOnly ParseDate(string iso) =>
        DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
