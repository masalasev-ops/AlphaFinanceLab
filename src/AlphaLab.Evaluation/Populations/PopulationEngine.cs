namespace AlphaLab.Evaluation.Populations;

/// <summary>One member's equity on one day, plus the realized one-way turnover fraction that produced it
/// (the raw material for the finding-115 turnover match).</summary>
public readonly record struct MemberDay(decimal Equity, double TurnoverOneWay);

/// <summary>
/// The pure population simulation (STRATEGY_CATALOG §5.2). Given a member's prior equity and the market,
/// it computes the day's equity deterministically: hold yesterday's equal-weight top-N, earn today's
/// return on it, then re-draw to today's top-N and pay the D43 cost of the turnover (cost-on families
/// only). No DB, no clock, no RNG — reconstructible from (seeds, dates, watermarked market) alone.
///
/// Batching (§5.2): <see cref="ComputeFamilyDay"/> selects over the member axis reusing one shared
/// eligible list + return lookup per day; the Worker bulk-inserts the results (no per-member EF round-trip).
/// </summary>
public sealed class PopulationEngine(IPopulationMarket market)
{
    private static readonly IReadOnlySet<long> EmptyHeld = new HashSet<long>();

    /// <summary>The member's equal-weight held set on <paramref name="date"/> — top-N by the deterministic
    /// score at the family's re-draw grid ordinal, tie-broken by security_id ascending (stable).</summary>
    public IReadOnlySet<long> Select(PopulationFamily family, int memberIndex, string date)
    {
        var eligible = market.Eligible(date);
        var grid = family.GridOrdinal(market.SessionOrdinal(date));
        // Partial top-N: sort the (score, id) pairs descending by score, then ascending by id.
        var scored = new List<(double Score, long Id)>(eligible.Count);
        foreach (var id in eligible)
            scored.Add((PopulationRng.Score(family.FamilySeed, memberIndex, grid, id), id));

        scored.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });

        var take = Math.Min(family.SelectionN, scored.Count);
        var held = new HashSet<long>(take);
        for (var i = 0; i < take; i++) held.Add(scored[i].Id);
        return held;
    }

    /// <summary>Advance one member one day. <paramref name="prevDate"/> is null on the member's inception
    /// day (no prior holdings ⇒ no return, an initial buy of the first held set).</summary>
    public MemberDay Step(PopulationFamily family, int memberIndex, decimal priorEquity, string? prevDate, string date)
    {
        var heldToday = Select(family, memberIndex, date);
        IReadOnlySet<long> heldPrev = prevDate is null ? EmptyHeld : Select(family, memberIndex, prevDate);

        // No prior holdings on the inception day ⇒ no return (the first held set is an initial buy).
        var grossReturn = heldPrev.Count > 0 ? heldPrev.Average(s => market.DailyReturn(s, date)) : 0.0;
        var turnover = SymmetricDifference(heldPrev, heldToday);      // both legs (drives cost)

        // Per-name equal weight follows the ACTUAL held count — the same convention as the gross return
        // above (each held name is 1/heldCount of a fully-invested book), NOT a fixed 1/SelectionN. A SELL
        // settles at yesterday's weight (1/heldPrev), a BUY at today's (1/heldToday). Weighting cost and
        // turnover by SelectionN instead would misprice both whenever the eligible universe is thinner than
        // SelectionN (heldCount < N) — the gross side would credit ~1/heldCount while cost charged ~1/N.
        //
        // Cost is charged on EVERY traded name (a spread on each buy and each sell — both legs). The REPORTED
        // turnover is one-way (the BUY leg only) so it matches the strategy comparator, which counts buy
        // notional only (StrategyMetrics.AnnualizedTurnover) — a two-way count would double the population's.
        var perNamePrev = heldPrev.Count > 0 ? priorEquity / heldPrev.Count : 0m;
        var perNameToday = heldToday.Count > 0 ? priorEquity / heldToday.Count : 0m;
        var buyCount = 0;
        var costDrag = 0.0;
        foreach (var s in turnover)
        {
            var isBuy = !heldPrev.Contains(s);                        // a buy = a newly entered name
            if (isBuy) buyCount++;
            if (family.CostsOn)
            {
                var (perName, weightDenom) = isBuy ? (perNameToday, heldToday.Count) : (perNamePrev, heldPrev.Count);
                costDrag += market.OneWayCostFraction(s, date, perName) / weightDenom;
            }
        }

        var equity = priorEquity * (decimal)(1.0 + grossReturn - costDrag);
        var turnoverOneWay = heldToday.Count > 0 ? (double)buyCount / heldToday.Count : 0.0;
        return new MemberDay(equity, turnoverOneWay);
    }

    /// <summary>Simulate one member across an ordered date list from a starting equity — the whole track
    /// in one pass (used by the batched daily compute and by tests/replay). Returns per-day equity.</summary>
    public IReadOnlyList<MemberDay> SimulateMember(PopulationFamily family, int memberIndex, decimal startEquity, IReadOnlyList<string> dates)
    {
        var result = new List<MemberDay>(dates.Count);
        var equity = startEquity;
        string? prev = null;
        foreach (var date in dates)
        {
            var day = Step(family, memberIndex, equity, prev, date);
            result.Add(day);
            equity = day.Equity;
            prev = date;
        }
        return result;
    }

    /// <summary>Compute every member of a family for a single day, given each member's prior equity
    /// (indexed by member). The shared eligible/return reads happen inside <see cref="Step"/> per member,
    /// but the family's per-day inputs come from one market instance — the §5.2 "one solve per family per
    /// day" seam (equal sizing in Phase 3, D-A; the covariance solve drops in here at Phase 6).</summary>
    public MemberDay[] ComputeFamilyDay(PopulationFamily family, IReadOnlyList<decimal> priorEquityByMember, string? prevDate, string date)
    {
        var days = new MemberDay[family.Size];
        for (var m = 0; m < family.Size; m++)
            days[m] = Step(family, m, priorEquityByMember[m], prevDate, date);
        return days;
    }

    private static IEnumerable<long> SymmetricDifference(IReadOnlySet<long> a, IReadOnlySet<long> b)
    {
        foreach (var x in a) if (!b.Contains(x)) yield return x;
        foreach (var x in b) if (!a.Contains(x)) yield return x;
    }
}
