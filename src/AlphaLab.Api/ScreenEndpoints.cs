using AlphaLab.Core.ReadModels;
using AlphaLab.Evaluation.ReadModels;

namespace AlphaLab.Api;

/// <summary>
/// Read endpoints — one per §15 screen (D57). Pure projections with no side effects. In Phase 0
/// they return empty, no_run_yet read-models (D66) and never open the DB; later phases replace the
/// bodies with real projections built in AlphaLab.Evaluation. A forward read-model can never carry a
/// replay row by construction — replay artifacts are served ONLY from /replay, flagged quarantined.
/// </summary>
public static class ScreenEndpoints
{
    public static RouteGroupBuilder MapScreenReadEndpoints(this RouteGroupBuilder group)
    {
        // D58 read-models: pure projections built in AlphaLab.Evaluation and rendered verbatim (the API
        // holds NO statistics/thresholds/verdict logic — D57). Forward-only, so a replay row can never
        // appear (FR-33). The builders resolve to no_run_yet before the first committed forward run.
        group.MapGet("/strategies", (StrategiesReadModelBuilder b) => TypedResults.Ok(b.Build()))
            .WithName("GetStrategies").WithSummary("Strategy leaderboard read-model.");

        group.MapGet("/strategies/{id}", (string id, StrategiesReadModelBuilder b) => TypedResults.Ok(b.BuildDetail(id)))
            .WithName("GetStrategyDetail").WithSummary("Single strategy card read-model.");

        group.MapGet("/live", () => TypedResults.Ok(LiveReadModel.NoRunYet))
            .WithName("GetLive").WithSummary("Live account read-model.");

        group.MapGet("/allocation", (AllocationReadModelBuilder b) => TypedResults.Ok(b.Build()))
            .WithName("GetAllocation").WithSummary("Ensemble allocation read-model.");

        group.MapGet("/cohort-maturation", (CohortMaturationBuilder b) => TypedResults.Ok(b.Build()))
            .WithName("GetCohortMaturation").WithSummary("Cohort maturation curve read-model (D88/FR-39).");

        group.MapGet("/go-live-log", () => TypedResults.Ok(GoLiveLogReadModel.NoRunYet))
            .WithName("GetGoLiveLog").WithSummary("Go-live / retire log read-model.");

        group.MapGet("/trades", () => TypedResults.Ok(TradesReadModel.NoRunYet))
            .WithName("GetTrades").WithSummary("Trades read-model.");

        group.MapGet("/health/overfitting", () => TypedResults.Ok(OverfittingHealthReadModel.NoRunYet))
            .WithName("GetOverfittingHealth").WithSummary("Overfitting monitor (eight signals) read-model.");

        group.MapGet("/regimes", () => TypedResults.Ok(RegimesReadModel.NoRunYet))
            .WithName("GetRegimes").WithSummary("Regime episodes read-model.");

        group.MapGet("/risk", () => TypedResults.Ok(RiskReadModel.NoRunYet))
            .WithName("GetRisk").WithSummary("Risk / guardrails read-model.");

        group.MapGet("/data-health", () => TypedResults.Ok(DataHealthReadModel.NoRunYet))
            .WithName("GetDataHealth").WithSummary("Data-health read-model.");

        group.MapGet("/journal", () => TypedResults.Ok(JournalReadModel.NoRunYet))
            .WithName("GetJournal").WithSummary("Hypothesis journal read-model.");

        group.MapGet("/why-trade/{strategyId}/{date}",
                (string strategyId, string date) => TypedResults.Ok(WhyTradeReadModel.NoRunYet))
            .WithName("GetWhyTrade").WithSummary("Why-trade explanation read-model.");

        group.MapGet("/admin/interventions", () => TypedResults.Ok(AdminInterventionsReadModel.NoRunYet))
            .WithName("GetAdminInterventions").WithSummary("Admin interventions (D55) read-model.");

        group.MapGet("/activity", () => TypedResults.Ok(ActivityReadModel.NoRunYet))
            .WithName("GetActivity").WithSummary("Activity feed read-model.");

        group.MapGet("/replay", (ReplayReadModelBuilder b) => TypedResults.Ok(b.Build()))
            .WithName("GetReplay").WithSummary("Quarantined Arena Replay artifacts (always flagged).");

        return group;
    }
}
