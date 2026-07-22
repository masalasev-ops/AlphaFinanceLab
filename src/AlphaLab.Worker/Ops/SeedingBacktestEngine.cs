using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Replay;
using AlphaLab.Strategies;
using AlphaLab.Worker.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>
/// The <see cref="IBacktestEngine"/> implementation (checkpoint 4.10): Arena Replay's RESTRICTED
/// special case — the same ReplayRunner (frozen watermark, date ceiling, as-of membership,
/// run_kind='replay') with plants OFF and the evaluation cadence OFF, so NOTHING judging is ever
/// written: no power_reports, no go_live_log, no overfitting rows, no allocations, no status change.
/// The output is a quarantined equity track + DESCRIPTIVE seeding metrics vs the cap-weight
/// benchmark — S1's future "backtest reference" input, never a promotion input.
///
/// Today only registry-known models can trade a replay day (the dummies; real IModels arrive with
/// Phase 6) — an unknown strategy fails closed rather than fabricating a track. Shares the D95
/// one-generation semantics with the calibration replay: a seeding run over a different watermark or
/// an evaluated generation must --reset (mixing evaluated and unevaluated days would corrupt the
/// generation's cadence bookkeeping).
/// </summary>
public sealed class SeedingBacktestEngine(
    IConfiguration configuration,
    ArenaOptions arena,
    string connectionString,
    ILoggerFactory loggerFactory) : IBacktestEngine
{
    private const string Replay = "replay";
    private const int DefaultLag = 21;

    public async Task<BacktestResult> RunAsync(BacktestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Phase2StrategyRegistry.For(request.StrategyId) is null)
        {
            throw new InvalidOperationException(
                $"'{request.StrategyId}' has no registered model — only registry-known strategies can trade a " +
                "replay day (real IModels arrive with Phase 6). Fabricating a track would be worse (fail closed).");
        }

        var outcome = await new ReplayRunner(configuration, arena, loggerFactory)
            .RunAsync(connectionString, new ReplayRequest(
                request.From, request.To, request.Watermark, LearnThrough: null,
                Reset: request.Reset, WithPlants: false, WithEvaluation: false), ct)
            .ConfigureAwait(false);
        if (outcome.StoppedEarly)
        {
            throw new InvalidOperationException(
                $"Seeding backtest stopped early at {outcome.SessionsCommitted}/{outcome.SessionsPlanned}: {outcome.StopReason}");
        }

        var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
        using var db = new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);

        var curve = ReplayCurve(db, request.StrategyId);
        var bench = ReplayCurve(db, Evaluation.EvaluationStep.DefaultBenchmarkStrategyId);
        if (curve.Count < 2)
        {
            throw new InvalidOperationException(
                $"'{request.StrategyId}' produced fewer than two replay equity points — nothing to summarize (fail closed).");
        }

        var (sr, br) = Aligned(curve, bench);
        var sharpe = sr.Count >= 2 ? StrategyMetrics.Sharpe(sr, 0.0) : 0.0;
        var alpha = 0.0;
        if (sr.Count >= 2)
        {
            try { alpha = StrategyMetrics.JensenAlpha(sr, br, 0.0, DefaultLag).AlphaAnnualized; }
            catch (ArgumentException)
            {
                double m = 0;
                for (var i = 0; i < sr.Count; i++) m += sr[i] - br[i];
                alpha = m / sr.Count * MetricsConstants.TradingDaysPerYear;
            }
        }

        return new BacktestResult(request.StrategyId, request.From, request.To, outcome.Watermark,
            curve.Select(c => new BacktestEquityPoint(c.AsOf, c.Equity)).ToList(), sharpe, alpha);
    }

    private static List<(string AsOf, decimal Equity)> ReplayCurve(AlphaLabDbContext db, string strategyId)
    {
        var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strategyId && a.RunKind == Replay);
        if (account is null) return [];
        return db.EquityCurve.Where(e => e.AccountId == account.AccountId && e.RunKind == Replay)
            .OrderBy(e => e.AsOf)
            .Select(e => new { e.AsOf, e.Equity })
            .AsEnumerable()
            .Select(e => (e.AsOf, e.Equity))
            .ToList();
    }

    private static (List<double> Strat, List<double> Bench) Aligned(
        IReadOnlyList<(string AsOf, decimal Equity)> strat, IReadOnlyList<(string AsOf, decimal Equity)> bench)
    {
        var benchByDate = bench.ToDictionary(b => b.AsOf, b => b.Equity, StringComparer.Ordinal);
        var common = strat.Where(s => benchByDate.ContainsKey(s.AsOf)).ToList();
        var sr = new List<double>();
        var br = new List<double>();
        for (var i = 1; i < common.Count; i++)
        {
            var sPrev = common[i - 1].Equity;
            var bPrev = benchByDate[common[i - 1].AsOf];
            if (sPrev <= 0 || bPrev <= 0) continue;
            sr.Add((double)(common[i].Equity / sPrev) - 1.0);
            br.Add((double)(benchByDate[common[i].AsOf] / bPrev) - 1.0);
        }
        return (sr, br);
    }
}
