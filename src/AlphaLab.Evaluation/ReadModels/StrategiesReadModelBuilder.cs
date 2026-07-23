using System.Globalization;
using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using DataStrategy = AlphaLab.Data.Entities.StrategyRow;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>
/// Builds the D58 Strategies leaderboard read-model (UX-1). Resolves each strategy's verdict chip, tier,
/// alpha MetricCell (dimmed inside the MDE, UX-1), population percentile, separation state (D63/FR-35), and
/// turnover caveat (finding 115) into DTO FIELDS — the client renders them verbatim. Reads only forward
/// (run_kind='live') rows, so a replay row can never appear (FR-33).
///
/// NOTE the name collision: <c>DataStrategy</c> is the persisted strategies row; the DTO it produces is
/// <see cref="StrategyRow"/> (AlphaLab.Core.ReadModels) — never conflate them.
/// </summary>
public sealed class StrategiesReadModelBuilder(AlphaLabDbContext db, VerdictsOptions verdicts)
{
    private const string RunKindLive = "live";

    public StrategiesReadModel Build()
    {
        var stamp = ReadModelStamps.LatestForward(db);
        if (stamp.Status == ReadModelStampStatus.NoRunYet) return StrategiesReadModel.NoRunYet;

        var rows = db.Strategies
            .OrderBy(s => s.StrategyId)
            .AsEnumerable()
            // D64 plants are REPLAY-ONLY fixtures (FR-36): they exist solely inside the quarantined
            // generation, so the forward leaderboard never lists them (rule 1 / FR-33).
            .Where(s => !Calibration.PlantCohorts.IsPlantId(s.StrategyId))
            .Select(BuildRow)
            .ToList();

        return new StrategiesReadModel { Stamp = stamp, Rows = rows };
    }

    public StrategyDetailReadModel BuildDetail(string strategyId)
    {
        var stamp = ReadModelStamps.LatestForward(db);
        var s = db.Strategies.FirstOrDefault(x => x.StrategyId == strategyId);
        if (stamp.Status == ReadModelStampStatus.NoRunYet || s is null) return StrategyDetailReadModel.NoRunYet;
        return new StrategyDetailReadModel { Stamp = stamp, Strategy = BuildRow(s) };
    }

    private StrategyRow BuildRow(DataStrategy s)
    {
        // Baselines/controls are reference rows — never a verdict, never allocated.
        if (s.Status is "baseline" or "control")
        {
            return new StrategyRow(s.StrategyId, s.StrategyId, IsLive: false, StrategyRow.SeatMath,
                VerdictChip: "reference", StrategyRow.TierReference, MetricCell.None,
                PopulationPercentile: null, Separation: null, TurnoverCaveat: false);
        }

        var power = db.PowerReports
            .Where(p => p.StrategyA == s.StrategyId && p.RunKind == RunKindLive)
            .OrderByDescending(p => p.AsOf).ThenByDescending(p => p.ReportId)
            .FirstOrDefault();
        var monitorStatus = db.OverfittingStatus
            .Where(o => o.StrategyId == s.StrategyId && o.RunKind == RunKindLive)
            .OrderByDescending(o => o.AsOf)
            .Select(o => o.Status)
            .FirstOrDefault();
        var s3 = db.OverfittingChecks
            .Where(c => c.StrategyId == s.StrategyId && c.Signal == "S3" && c.RunKind == RunKindLive)
            .OrderByDescending(c => c.AsOf).ThenByDescending(c => c.CheckId)
            .FirstOrDefault();
        var turnoverCaveat = db.OverfittingChecks
            .Where(c => c.StrategyId == s.StrategyId && c.Signal == "turnover_match" && c.RunKind == RunKindLive)
            .OrderByDescending(c => c.AsOf).ThenByDescending(c => c.CheckId)
            .Select(c => c.Contribution)
            .FirstOrDefault() == "caveat";

        var verdict = power?.Verdict ?? "TooEarly";     // no forward evidence yet ⇒ TooEarly

        // α cell (UX-1): the observed gap, DIMMED with a tilde while the verdict is TooEarly. The reason
        // distinguishes an inside-the-MDE gap (genuine noise) from a merely-too-short track — the gate
        // returns TooEarly for BOTH, but a short-track cell must not claim the gap is "within noise".
        MetricCell alpha;
        if (power is { ObservedGapAnn: { } gap })
        {
            var mde = new MetricMde(power.MdeAnn);
            if (verdict == "TooEarly")
            {
                var reason = Math.Abs(gap) < power.MdeAnn ? MetricCell.ReasonInsideMde : MetricCell.ReasonTooEarly;
                alpha = MetricCell.Dimmed(gap, FormatPct(gap), reason, mde);
            }
            else
            {
                alpha = MetricCell.Normal(gap, FormatPct(gap), mde);
            }
        }
        else
        {
            alpha = MetricCell.None;
        }

        var percentile = s3?.Value is { } pct ? new PopulationPercentile(pct, ExtractN(s3!.ThresholdJson)) : null;
        var separation = SeparationState.Resolve(db, s.StrategyId, verdicts, RunKindLive);
        var tier = ResolveTier(verdict, s.Status, monitorStatus, separation);

        return new StrategyRow(s.StrategyId, s.StrategyId, s.Status == "live", StrategyRow.SeatMath,
            verdict, tier, alpha, percentile, separation, turnoverCaveat);
    }

    private static string ResolveTier(string verdict, string status, string? monitorStatus, Core.ReadModels.SeparationInfo? separation)
    {
        if (monitorStatus is "suspect" or "retired" || status == "retired") return StrategyRow.TierBelowOrFlagged;
        // A Refused verdict is distinguishable DOWNWARD — separation_state is direction-agnostic (D63/§20.8),
        // so it must be checked BEFORE 'distinguishable-above' or a below-benchmark loser would flatter-sort
        // into the top tier.
        if (verdict == "Refused") return StrategyRow.TierBelowOrFlagged;
        if (separation?.State == Core.ReadModels.SeparationInfo.Distinguishable || verdict == "Promoted" || status == "live")
            return StrategyRow.TierDistinguishableAbove;
        return StrategyRow.TierNotYetDistinguishable;   // TooEarly / no evidence / emerging
    }

    private static string FormatPct(double fraction)
    {
        var pct = fraction * 100.0;
        var sign = pct >= 0 ? "+" : "";
        return $"{sign}{pct.ToString("F1", CultureInfo.InvariantCulture)}%";
    }

    private static int ExtractN(string thresholdJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(thresholdJson);
            return doc.RootElement.TryGetProperty("n", out var n) && n.TryGetInt32(out var v) ? v : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
