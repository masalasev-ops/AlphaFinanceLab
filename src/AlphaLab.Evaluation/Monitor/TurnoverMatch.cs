using System.Text.Json;
using AlphaLab.Core.Json;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.Monitor;

/// <summary>The turnover-match outcome: the population's median turnover + the ±tolerance band, and
/// whether the strategy's realized turnover falls outside it (the cost-match caveat).</summary>
public readonly record struct TurnoverMatchResult(double StrategyTurnover, double PopulationMedian, double BandLo, double BandHi, bool Caveat);

/// <summary>
/// Turnover-match verification (finding 115). A strategy that churns materially LESS than its
/// turnover-matched population pays less cost drag than its own null, biasing the S3 percentile
/// anti-conservatively (the one direction the lab must never err) — so a strategy whose realized
/// annualized turnover is outside ±TurnoverMatchTolerancePct of its population's median renders the
/// cost-match caveat.
///
/// The caveat is persisted as an overfitting_checks signal='turnover_match' row with a NEUTRAL
/// contribution — it is DESCRIPTIVE (a cost-comparability disclosure), never a verdict input, so the
/// monitor's status aggregation (S2/S3/S6 only) can never read it (FX-TurnoverMatch-StatusNeutral).
/// </summary>
public static class TurnoverMatch
{
    public const string Signal = "turnover_match";
    public const string NeutralContributionMatched = "matched";
    public const string NeutralContributionCaveat = "caveat";   // still status-neutral — never aggregated

    public static TurnoverMatchResult Check(double strategyTurnover, IReadOnlyList<double> populationTurnovers, double tolerancePct)
    {
        var median = Statistics.Percentile(populationTurnovers, 50);
        var lo = median * (1.0 - tolerancePct / 100.0);
        var hi = median * (1.0 + tolerancePct / 100.0);
        var caveat = !double.IsNaN(median) && (strategyTurnover < lo || strategyTurnover > hi);
        return new TurnoverMatchResult(strategyTurnover, median, lo, hi, caveat);
    }

    /// <summary>Persist the turnover_match row for a strategy (the caller owns the transaction). Returns
    /// whether the caveat fired. contribution is 'caveat'/'matched' but ALWAYS status-neutral.</summary>
    public static bool WriteCheck(AlphaLabDbContext db, string asOf, string strategyId,
        double strategyTurnover, IReadOnlyList<double> populationTurnovers, double tolerancePct, string runKind = "live")
    {
        var result = Check(strategyTurnover, populationTurnovers, tolerancePct);
        db.OverfittingChecks.Add(new OverfittingCheckRow
        {
            StrategyId = strategyId,
            AsOf = asOf,
            Signal = Signal,
            Value = strategyTurnover,
            ThresholdJson = JsonSerializer.Serialize(
                new { population_median = result.PopulationMedian, band_lo = result.BandLo, band_hi = result.BandHi, tolerance_pct = tolerancePct, n = populationTurnovers.Count },
                AlphaLabJson.Options),
            Contribution = result.Caveat ? NeutralContributionCaveat : NeutralContributionMatched,
            RunKind = runKind,
        });
        db.SaveChanges();
        return result.Caveat;
    }
}
