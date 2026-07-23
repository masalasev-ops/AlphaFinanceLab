using AlphaLab.Data.Services;

namespace AlphaLab.Worker.Pipeline;

/// <summary>What one trading day looks like to a pipeline extension (a snapshot of the Stage-2 inputs
/// the extension may read; it writes through its own injected services inside the SAME transaction).</summary>
public sealed record PipelineDayContext(
    string AsOf, DateOnly AsOfDate, string Watermark, string RunKindToken, BarFeatureView Features);

/// <summary>
/// A hook inside the Stage-2 transaction, invoked AFTER the control populations compute (Phase 4 /
/// checkpoint 4.5). The FORWARD composition registers none — the forward day is exactly what it was.
/// The REPLAY composition registers <see cref="PlantEquityStep"/>, which is how the D64 plants join the
/// atomic day without cutting a plant-shaped seam into DailyPipeline's body.
/// </summary>
public interface IPipelineDayExtension
{
    /// <summary>Runs inside the Stage-2 transaction, after populations. A throw rolls the day back.</summary>
    void AfterPopulations(PipelineDayContext context);
}

/// <summary>
/// Whether the day runs the 21-day evaluation cadence (gate → monitor → allocator). Default TRUE
/// everywhere; ONLY the walk-forward seeding engine (4.10) registers false — the IBacktestEngine is
/// replay's restricted special case and must never judge promotions (no power_reports, no
/// go_live_log, no overfitting rows, no allocations). Structural: the cadence simply never fires.
/// </summary>
public sealed class PipelineEvaluationToggle
{
    public bool Enabled { get; init; } = true;
}
