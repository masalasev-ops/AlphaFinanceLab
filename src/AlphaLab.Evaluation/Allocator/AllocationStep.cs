using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.Json;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Power;

namespace AlphaLab.Evaluation.Allocator;

/// <summary>
/// Runs the D51 ensemble allocator for one evaluation and persists the full input vector to allocation_log
/// (FR-27 / NFR-2). Reads the power_reports the gate just wrote (α̂ = observed gap; se = MDE / z-sum) joined
/// with the overfitting_status the monitor just wrote (Suspect), plus the prior allocation for the band
/// hysteresis. Baselines/controls are excluded by construction (only power_reports rows — candidate/live vs
/// the benchmark — are read). Writes via the caller's transaction (D59).
/// </summary>
public sealed class AllocationStep(AlphaLabDbContext db, GateOptions gate, AllocatorOptions allocator)
{
    private const string RunKindLive = "live";

    /// <summary>The compact per-strategy weight persisted to allocation_log.weights_json (snake_case).</summary>
    public sealed record WeightEntry(
        string StrategyId, double AlphaHatPct, double SePct, double ShrinkWeight, double AlphaTildePct,
        double Target, double Applied, double Weight, IReadOnlyList<string> ClampsBound);

    public AllocationOutcome Run(string asOf, string runKind = RunKindLive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var reports = db.PowerReports
            .Where(p => p.AsOf == asOf && p.RunKind == runKind)
            .Select(p => new { p.StrategyA, p.ObservedGapAnn, p.MdeAnn, p.Verdict })
            .ToList();
        if (reports.Count == 0) return new AllocationOutcome([], "no_reports");

        var status = db.OverfittingStatus
            .Where(o => o.AsOf == asOf && o.RunKind == runKind)
            .ToDictionary(o => o.StrategyId, o => o.Status);

        var prior = PriorWeights(asOf, runKind);
        var zsum = MdeCalculator.ZSum(gate.Confidence, gate.Power);

        var inputs = new List<AllocationInput>(reports.Count);
        foreach (var r in reports)
        {
            var alphaPct = (r.ObservedGapAnn ?? 0.0) * 100.0;
            var sePct = double.IsFinite(r.MdeAnn) ? r.MdeAnn / zsum * 100.0 : double.PositiveInfinity;
            var tooEarly = r.Verdict == "TooEarly";
            var suspect = status.TryGetValue(r.StrategyA, out var s) && s == "suspect";
            inputs.Add(new AllocationInput(r.StrategyA, alphaPct, sePct, tooEarly, suspect,
                prior.TryGetValue(r.StrategyA, out var pw) ? pw : null));
        }

        var outcome = EnsembleAllocator.Allocate(inputs, allocator);

        var entries = outcome.Rows.Select(x => new WeightEntry(
            x.StrategyId, x.AlphaHatPct, x.SePct, x.ShrinkWeight, x.AlphaTildePct, x.Target, x.Applied, x.Weight, x.ClampsBound));
        db.AllocationLog.Add(new AllocationLogRow
        {
            AsOf = asOf,
            WeightsJson = JsonSerializer.Serialize(entries, AlphaLabJson.Options),
            Reason = outcome.Reason,
            RunKind = runKind,
        });
        db.SaveChanges();

        return outcome;
    }

    private Dictionary<string, double> PriorWeights(string asOf, string runKind)
    {
        var latest = db.AllocationLog
            .Where(a => a.RunKind == runKind && string.Compare(a.AsOf, asOf) < 0)
            .OrderByDescending(a => a.AsOf)
            .Select(a => a.WeightsJson)
            .FirstOrDefault();

        if (latest is null) return [];
        var entries = JsonSerializer.Deserialize<List<WeightEntry>>(latest, AlphaLabJson.Options) ?? [];
        return entries.ToDictionary(e => e.StrategyId, e => e.Weight);
    }
}
