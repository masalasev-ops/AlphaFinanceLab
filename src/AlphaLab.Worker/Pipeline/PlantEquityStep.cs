using AlphaLab.Core.Config;
using AlphaLab.Core.Ledger;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Populations;
using AlphaLab.Strategies;

namespace AlphaLab.Worker.Pipeline;

/// <summary>
/// The D64 plants' daily equity (FR-36, MASTER §20.9), computed inside the replay Stage-2 transaction.
/// A plant = a fresh-seeded population member at a DEDICATED member index (same family breadth, sizing,
/// re-draw cadence and cost model as the real population, by construction — it IS a PopulationEngine
/// member) + the overlay: equity_t = equity_{t−1} × (1 + r_member + overlay_t). Only equity_curve rows
/// are written (run_kind='replay') — the monitor (S2/S3/S6), gate, and allocator consume equity curves,
/// so the whole downstream machinery sees plants organically; no trades/positions are fabricated.
/// The overlay's regime conditioning reads the PIT REPLAY regime label (D93) written earlier in this
/// same Stage-2 transaction, and its running-mix normalizer uses labels ≤ asOf only (leak-free).
/// </summary>
public sealed class PlantEquityStep(
    AlphaLabDbContext db,
    ILedgerStore ledger,
    PopulationsOptions populations,
    CostsOptions costs,
    ICalendarService calendar,
    IIndexMembershipRead membership,
    CalibrationOptions calibration,
    DataQualityOptions dataQuality) : IPipelineDayExtension
{
    private const string Replay = "replay";

    public void AfterPopulations(PipelineDayContext context)
    {
        if (context.RunKindToken != Replay) return;   // defensive: this step is replay-composition-only

        var accounts = db.Accounts.Where(a => a.RunKind == Replay).ToList()
            .Where(a => PlantCohorts.IsPlantId(a.StrategyId))
            .ToDictionary(a => a.StrategyId, a => a.AccountId, StringComparer.Ordinal);
        if (accounts.Count == 0) return;              // a plantless replay (e.g. the seeding mode)

        var families = PopulationFamilies.ForPhase3(populations)
            .Where(f => f.CostsOn)
            .ToDictionary(f => f.Name, StringComparer.Ordinal);
        var specs = PlantCohorts.Build(calibration.Plant, PopulationFamilies.ForPhase3(populations));
        var market = new PopulationMarket(context.Features, membership, calendar, new CostModel(costs), costs.AdvWindowDays, dataQuality.MaxSingleDayPriceFactor);
        var engine = new PopulationEngine(market);

        var prevSession = calendar.PreviousSession(context.AsOfDate);
        var prevDate = prevSession?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // One query: every plant's prior-session equity (absent ⇒ that plant's inception is today).
        var accountIds = accounts.Values.ToList();
        var prior = prevDate is null
            ? []
            : db.EquityCurve
                .Where(e => e.AsOf == prevDate && e.RunKind == Replay && accountIds.Contains(e.AccountId))
                .ToDictionary(e => e.AccountId, e => e.Equity);

        // The overlay ordinal: sessions since plant inception (all cohorts seed together) = committed
        // plant equity rows before today. One COUNT on a stable representative account.
        var representative = accountIds.Min();
        var ordinal = db.EquityCurve.Count(e =>
            e.RunKind == Replay && e.AccountId == representative && string.Compare(e.AsOf, context.AsOf) < 0);

        // The PIT regime conditioning (D64/D93): today's REPLAY label (written earlier in this Stage-2
        // txn) and the running bull/bear mix ≤ today for the renormalizer — labels ≤ t only, leak-free.
        var label = db.RegimeLabels.Find(context.AsOf, Replay);
        var plant = calibration.Plant;
        var multiplier = label is null ? 1.0 : plant.MultiplierFor(label.Trend);
        var bulls = db.RegimeLabels.Count(l => l.RunKind == Replay && l.Trend == "bull" && string.Compare(l.AsOf, context.AsOf) <= 0);
        var bears = db.RegimeLabels.Count(l => l.RunKind == Replay && l.Trend == "bear" && string.Compare(l.AsOf, context.AsOf) <= 0);
        var runningMean = PlantOverlay.RunningMultiplierMean(bulls, bears, plant.MultiplierFor("bull"), plant.MultiplierFor("bear"));

        foreach (var spec in specs)
        {
            if (!accounts.TryGetValue(spec.StrategyId, out var accountId)) continue;
            var family = families[spec.Family];

            var inception = !prior.TryGetValue(accountId, out var priorEquity);
            if (inception) priorEquity = DummyRoster.DefaultStartingCash;

            var day = engine.Step(family, spec.MemberIndex, priorEquity, inception ? null : prevDate, context.AsOf);
            var memberReturn = priorEquity > 0 ? (double)(day.Equity / priorEquity) - 1.0 : 0.0;
            var overlay = PlantOverlay.OverlayReturn(
                spec.Kind, spec.AlphaAnnPct, spec.Key, ordinal,
                plant.ActiveDayFrac, PlantOverlay.MeanActiveRun(plant.PersistencePhi, spec.HorizonDays),
                multiplier, runningMean);

            var equity = priorEquity * (decimal)(1.0 + memberReturn + overlay);
            ledger.RecordEquityPoint(accountId, context.AsOf, equity, 0m, RunKind.Replay);
        }
    }
}
