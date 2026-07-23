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
            .Select(r => new { r.RunId, r.AsOf, r.Watermark })
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
            // The stamp carries the REPLAY generation's provenance (rule 20/D60/D66): its latest run's
            // run_id, the generation's frozen watermark, and the last replayed session — never a
            // forward run's identity (Phase-4 review: LatestForward here misattributed the quarantined
            // screen to an unrelated forward run, and served NoRunYet on a replay-only store).
            Stamp = ReadModelStamp.Stamped(runs[^1].RunId, runs[0].Watermark, runs[^1].AsOf),
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
