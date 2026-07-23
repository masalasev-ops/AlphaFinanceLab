using System.Text.Json;
using AlphaLab.Core.Json;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Evaluation.Allocator;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>Builds the D51 allocation read-model (UX-9) from the latest forward allocation_log row — the
/// reconstructible per-strategy derivation vector (α̂ ± se → α̃ → target → applied → weight, with the
/// clamps that bound it). Forward-only (run_kind='live'), FR-33.</summary>
public sealed class AllocationReadModelBuilder(AlphaLabDbContext db)
{
    private const string RunKindLive = "live";

    public AllocationReadModel Build()
    {
        var stamp = ReadModelStamps.LatestForward(db);
        if (stamp.Status == ReadModelStampStatus.NoRunYet) return AllocationReadModel.NoRunYet;

        var weightsJson = db.AllocationLog
            .Where(a => a.RunKind == RunKindLive)
            .OrderByDescending(a => a.AsOf).ThenByDescending(a => a.EventId)
            .Select(a => a.WeightsJson)
            .FirstOrDefault();

        if (weightsJson is null) return new AllocationReadModel { Stamp = stamp, Rows = [], AllocatorValueAdd = ValueAdd() };

        var entries = JsonSerializer.Deserialize<List<AllocationStep.WeightEntry>>(weightsJson, AlphaLabJson.Options) ?? [];
        var rows = entries
            .Select(e => new AllocationRowDto(e.StrategyId, e.AlphaHatPct, e.SePct, e.AlphaTildePct, e.Target, e.Applied, e.Weight, e.ClampsBound))
            .ToList();

        return new AllocationReadModel { Stamp = stamp, Rows = rows, AllocatorValueAdd = ValueAdd() };
    }

    // The §1.2 allocator value-add KPI (Phase 4): read from the ONE quarantined replay pair row the
    // KPI computation persisted — replay-sourced by construction, marked so, D60 money-as-strings.
    private AllocatorValueAddDto? ValueAdd()
    {
        var row = db.PowerReports
            .Where(p => p.RunKind == "replay" && p.StrategyA == Calibration.AllocatorValueAddKpi.BlendId)
            .OrderByDescending(p => p.AsOf)
            .FirstOrDefault();
        if (row is null) return null;
        return new AllocatorValueAddDto(
            ((row.ObservedGapAnn ?? 0) * 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            (row.MdeAnn * 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            row.TDays, row.Verdict ?? "TooEarly", "replay", Quarantined: true);
    }
}
