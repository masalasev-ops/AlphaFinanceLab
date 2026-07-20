using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Evaluation.Numerics;
using DataStrategy = AlphaLab.Data.Entities.StrategyRow;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>
/// Builds the D88/FR-39 cohort maturation curve. Groups promotable strategies (candidate/live/retired —
/// retired RETAINED, no survivorship) into admission-vintage cohorts by strategies.created_on / the
/// Kpi.CohortBucketMonths bucket, and plots each cohort's MEDIAN D36 population percentile (reused verbatim
/// from the persisted S3 rows — never a second percentile) against track length t in trading days,
/// AGE-ALIGNED by evaluation index (never wall-clock). Rails resolved into the data: thin cohorts + sub-MDE
/// cohort gaps ship display='dimmed'; replay cohorts are quarantined and never co-plotted. Descriptive only.
/// </summary>
public sealed class CohortMaturationBuilder(AlphaLabDbContext db, KpiOptions kpi, GateOptions gate)
{
    public CohortMaturationReadModel Build()
    {
        var stamp = ReadModelStamps.LatestForward(db);
        if (stamp.Status == ReadModelStampStatus.NoRunYet) return CohortMaturationReadModel.NoRunYet;

        var strategies = db.Strategies
            .Where(s => s.Status == "candidate" || s.Status == "live" || s.Status == "retired")   // retired retained
            .OrderBy(s => s.StrategyId)
            .ToList();

        var forward = BuildCohorts(strategies, "live", quarantined: false);
        ApplyInsideMdeDimming(forward);   // forward cohorts compare to the oldest at equal t
        var replay = BuildCohorts(strategies, "replay", quarantined: true);

        return new CohortMaturationReadModel { Stamp = stamp, Cohorts = [.. forward, .. replay] };
    }

    private List<Cohort> BuildCohorts(IReadOnlyList<DataStrategy> strategies, string runKind, bool quarantined)
    {
        var cohorts = new List<Cohort>();
        var groups = strategies
            .GroupBy(s => BucketKey(s.CreatedOn))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Bucket);

        foreach (var group in groups)
        {
            // Each member's S3 percentile path, ordered — age-aligned by EVALUATION INDEX (all on the 21-day cadence).
            var paths = group
                .Select(s => S3Path(s.StrategyId, runKind))
                .Where(p => p.Count > 0)
                .ToList();
            if (paths.Count == 0) continue;

            var maxEvals = paths.Max(p => p.Count);
            var series = new List<CohortPoint>(maxEvals);
            for (var k = 0; k < maxEvals; k++)
            {
                var atK = paths.Where(p => p.Count > k).Select(p => p[k]).ToList();
                if (atK.Count == 0) continue;

                var t = (k + 1) * gate.EvaluationCadenceDays;
                var median = Statistics.Percentile(atK, 50);
                var lo = Statistics.Percentile(atK, 25);
                var hi = Statistics.Percentile(atK, 75);

                // Thin cohort dimming takes precedence (the strongest caveat).
                var (display, reason) = atK.Count < kpi.CohortMinStrategies
                    ? (CohortPoint.DisplayDimmed, (string?)CohortPoint.ReasonThinCohort)
                    : (CohortPoint.DisplayNormal, null);

                series.Add(new CohortPoint(t, atK.Count, median, lo, hi, display, reason));
            }

            cohorts.Add(new Cohort(Label(group.Key), group.Count(), quarantined, series));
        }

        return cohorts;
    }

    // A later cohort's gain vs the OLDEST cohort at equal t is "inside MDE" (not a real improvement) when it
    // is smaller than the pooled band half-width — dim it (never claim it), unless the point is already thin.
    private static void ApplyInsideMdeDimming(List<Cohort> forward)
    {
        if (forward.Count < 2) return;
        var baseline = forward[0].Series.ToDictionary(p => p.T);

        for (var c = 1; c < forward.Count; c++)
        {
            var updated = new List<CohortPoint>(forward[c].Series.Count);
            foreach (var p in forward[c].Series)
            {
                if (p.Reason == CohortPoint.ReasonThinCohort || !baseline.TryGetValue(p.T, out var b))
                {
                    updated.Add(p);
                    continue;
                }

                var gap = Math.Abs(p.MedianPercentile - b.MedianPercentile);
                var tolerance = Math.Max(HalfBand(p), HalfBand(b));   // pooled band half-width ~ the gap's MDE
                updated.Add(gap < tolerance
                    ? p with { Display = CohortPoint.DisplayDimmed, Reason = CohortPoint.ReasonInsideMde }
                    : p);
            }
            forward[c] = forward[c] with { Series = updated };
        }
    }

    private static double HalfBand(CohortPoint p) => Math.Max(p.BandHi - p.MedianPercentile, p.MedianPercentile - p.BandLo);

    private List<double> S3Path(string strategyId, string runKind) =>
        db.OverfittingChecks
            .Where(c => c.StrategyId == strategyId && c.Signal == "S3" && c.RunKind == runKind && c.Value != null)
            .OrderBy(c => c.AsOf).ThenBy(c => c.CheckId)
            .Select(c => c.Value!.Value)
            .ToList();

    private (int Year, int Bucket) BucketKey(string createdOn)
    {
        var d = DateOnly.ParseExact(createdOn, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (d.Year, (d.Month - 1) / kpi.CohortBucketMonths);
    }

    private string Label((int Year, int Bucket) key) =>
        kpi.CohortBucketMonths == 6
            ? $"H{key.Bucket + 1} {key.Year}"
            : $"{key.Year}-{key.Bucket * kpi.CohortBucketMonths + 1:D2}+{kpi.CohortBucketMonths}mo";
}
