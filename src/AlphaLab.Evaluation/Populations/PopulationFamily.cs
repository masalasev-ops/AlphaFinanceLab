using AlphaLab.Core.Config;

namespace AlphaLab.Evaluation.Populations;

/// <summary>
/// A random control-population family (STRATEGY_CATALOG §5.2). <see cref="RedrawIntervalDays"/> is the
/// cadence: the member re-draws its scores on a fixed session-grid interval and holds between (daily=1,
/// banded=5, monthly=21). <see cref="CostsOn"/> distinguishes the cost-on families from the display-only
/// cost-free band. <see cref="Name"/> is the SCHEMA control_populations.family token (the cost-free band
/// is the daily family's costs-off twin — same name, same seed, costs_on=0).
/// </summary>
public sealed record PopulationFamily(
    string Name, int FamilySeed, int SelectionN, int RedrawIntervalDays, bool CostsOn, int Size)
{
    /// <summary>The re-draw grid ordinal for a date: the date's day-number floored to the interval, so
    /// scores are constant within an interval (turnover only on grid boundaries) and reconstructible.</summary>
    public long GridOrdinal(DateOnly date) => date.DayNumber / RedrawIntervalDays * RedrawIntervalDays;
}

/// <summary>
/// Builds the Phase-3 population families from <see cref="PopulationsOptions"/>. Three cost-on cadence
/// families (daily/banded/monthly) at M=Size, plus the cost-free daily twin at M=CostFreeSize (reuses the
/// daily seed so cost-free member i is exactly cost-on daily member i MINUS the cost drag — the direct
/// gross-vs-net demonstration; FX-PopBands). The quarterly seed is DORMANT this phase (spawned on demand
/// when a Phase-8 quarterly strategy enters, never speculatively); RandomPop-Event likewise.
/// </summary>
public static class PopulationFamilies
{
    /// <summary>Representative selection breadth (decile-like, §6.1) until per-family matching to real
    /// strategies arrives. There is no CONFIG key for it (populations are seeded, not tuned).</summary>
    public const int DefaultSelectionN = 40;

    public const int DailyInterval = 1;
    public const int BandedInterval = 5;
    public const int MonthlyInterval = 21;

    public static IReadOnlyList<PopulationFamily> ForPhase3(PopulationsOptions o, int selectionN = DefaultSelectionN) =>
    [
        new("daily", o.FamilySeeds.Daily, selectionN, DailyInterval, CostsOn: true, o.Size),
        new("banded", o.FamilySeeds.Banded, selectionN, BandedInterval, CostsOn: true, o.Size),
        new("monthly", o.FamilySeeds.Monthly, selectionN, MonthlyInterval, CostsOn: true, o.Size),
        new("daily", o.FamilySeeds.Daily, selectionN, DailyInterval, CostsOn: false, o.CostFreeSize),
    ];
}
