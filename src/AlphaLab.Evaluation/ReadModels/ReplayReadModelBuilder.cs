using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Evaluation.Calibration;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>
/// The /replay screen's read-model (D58/§22): a SUMMARY of the quarantined replay generation —
/// window, watermark, roster/plant counts, the allocator value-add pair — always Quarantined=true,
/// served ONLY from /api/v1/replay. Deliberately a summary, not the row-level artifacts: replay
/// per-member ledgers are prunable after sign-off (Replay.PrunePerMemberLedgersAfterSignoff), so
/// nothing here may depend on their survival.
/// </summary>
public sealed class ReplayReadModelBuilder(AlphaLabDbContext db)
{
    private const string Replay = "replay";

    public ReplayReadModel Build()
    {
        var runs = db.Runs
            .Where(r => r.RunKind == Replay && r.Status == "ok")
            .OrderBy(r => r.AsOf)
            .Select(r => new { r.AsOf, r.Watermark })
            .ToList();
        if (runs.Count == 0) return ReplayReadModel.NoRunYet;

        var plantStrategies = db.Accounts.Where(a => a.RunKind == Replay).Select(a => a.StrategyId).ToList()
            .Where(PlantCohorts.IsPlantId)
            .Count();
        var valueAdd = db.PowerReports
            .Where(p => p.RunKind == Replay && p.StrategyA == AllocatorValueAddKpi.BlendId)
            .OrderByDescending(p => p.AsOf)
            .Select(p => new { p.ObservedGapAnn, p.MdeAnn, p.Verdict, p.TDays })
            .FirstOrDefault();

        return new ReplayReadModel
        {
            // The stamp carries the REPLAY generation's provenance, not a forward run's.
            Stamp = ReadModelStamps.LatestForward(db),
            Quarantined = true,
            Rows =
            [
                new
                {
                    window_from = runs[0].AsOf,
                    window_to = runs[^1].AsOf,
                    sessions = runs.Count,
                    watermark = runs[0].Watermark,
                    plant_strategies = plantStrategies,
                    replay_accounts = db.Accounts.Count(a => a.RunKind == Replay),
                    evaluations = db.OverfittingStatus.Where(o => o.RunKind == Replay).Select(o => o.AsOf).Distinct().Count(),
                    allocator_value_add = valueAdd is null
                        ? null
                        : (object)new
                        {
                            gap_ann_pct = ((valueAdd.ObservedGapAnn ?? 0) * 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                            mde_ann_pct = (valueAdd.MdeAnn * 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                            verdict = valueAdd.Verdict,
                            t_days = valueAdd.TDays,
                        },
                },
            ],
        };
    }
}
