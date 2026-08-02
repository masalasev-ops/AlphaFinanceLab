using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Evaluation.Gate;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Power;

namespace AlphaLab.Evaluation.Recompute;

/// <summary>One recomputed pair evaluation — the gate's two artefacts for a (strategy, as-of): the
/// verdict, and the observed effect the verdict was taken on.</summary>
public sealed record RecomputedVerdict(
    string StrategyId, string AsOf, double ObservedEffectAnn, double MdeAnn, int TDays, PromotionVerdict Verdict);

/// <summary>
/// Re-derives the promotion gate's verdicts from the stored <c>equity_curve</c> — the
/// <see cref="RecomputeTier.EquityDerived"/> tier (finding 339: §25.2's original table had no row for this,
/// though its own prose listed "the alpha definition" as covered).
///
/// **It reproduces finding 285's defect faithfully by default, and that is the point.** Under
/// <c>gate.alpha_definition = raw_gap</c> the effect is <c>mean(strat − bench) × 252</c> with no beta term
/// — exactly what <c>EvaluationStep.cs:83-88</c> computes and exactly what D26 and rule 6 forbid. §25.1 is
/// explicit that the harness must reproduce the known defect before it can be trusted to evaluate the fix
/// for it; `jensen` substitutes <see cref="StrategyMetrics.JensenAlpha"/> at that same call site.
///
/// **Point-in-time by truncation.** The live gate runs inside a session and therefore sees the curve as it
/// stood that day. The recompute loads each curve ONCE and slices it at each evaluation's as-of, which is
/// the same series — never the full curve, which would give every early session the benefit of later data.
/// </summary>
public sealed class GateRecompute(AlphaLabDbContext db, RecomputeSpec spec, GateOptions gate, string runKind = "replay")
{
    private const int DefaultHorizonDays = 21;

    public IReadOnlyList<RecomputedVerdict> Run(IReadOnlyCollection<string> subjects, string benchmarkStrategyId)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        var useJensen = spec.Text(RecomputeParameters.AlphaDefinition, RecomputeParameters.AlphaRawGap)
            == RecomputeParameters.AlphaJensen;

        var benchAccount = db.Accounts.FirstOrDefault(a => a.StrategyId == benchmarkStrategyId && a.RunKind == runKind);
        if (benchAccount is null) return [];
        var benchCurve = CurveMath.Curve(db, benchAccount.AccountId, runKind);
        if (benchCurve.Count < 2) return [];

        var benchHorizon = db.Strategies
            .Where(s => s.StrategyId == benchmarkStrategyId)
            .Select(s => s.HoldingHorizonDays)
            .FirstOrDefault() ?? DefaultHorizonDays;

        var results = new List<RecomputedVerdict>();
        foreach (var strategyId in subjects.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (strategyId == benchmarkStrategyId) continue;

            var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strategyId && a.RunKind == runKind);
            if (account is null) continue;
            var stratCurve = CurveMath.Curve(db, account.AccountId, runKind);
            if (stratCurve.Count < 2) continue;

            var maxHorizon = Math.Max(
                db.Strategies.Where(s => s.StrategyId == strategyId).Select(s => s.HoldingHorizonDays).FirstOrDefault()
                    ?? DefaultHorizonDays,
                benchHorizon);

            // The sessions this pair was actually evaluated on (the gate runs on a cadence, not daily), read
            // from the stored power_reports so the recompute answers on exactly the same days.
            var sessions = db.PowerReports
                .Where(p => p.StrategyA == strategyId && p.StrategyB == benchmarkStrategyId && p.RunKind == runKind)
                .Select(p => p.AsOf)
                .Distinct()
                .AsEnumerable()
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();

            foreach (var asOf in sessions)
            {
                var s = Truncate(stratCurve, asOf);
                var b = Truncate(benchCurve, asOf);
                if (s.Count < 2 || b.Count < 2) continue;

                var (stratReturns, benchReturns) = CurveMath.AlignedReturns(s, b);
                if (stratReturns.Count < 2) continue;

                var d = new double[stratReturns.Count];
                for (var i = 0; i < d.Length; i++) d[i] = stratReturns[i] - benchReturns[i];

                var mde = MdeCalculator.Compute(d, maxHorizon, gate);

                // THE one line finding 285 is about. `raw_gap` is generation 1's behaviour, bug included.
                //
                // The corrected arm uses the MDE's OWN Newey-West lag rather than a separate default: the
                // gate compares the effect against `mde.MdeAnn`, and two numbers on two different lag
                // conventions are not comparable. Recorded here because the corrected-alpha pass inherits
                // the choice, and `rfDaily: 0.0` carries finding 285's second-order note forward — the
                // French RF series is Phase 6, so even the beta-adjusted arm is not yet RF-correct.
                var effect = useJensen
                    ? StrategyMetrics.JensenAlpha(stratReturns, benchReturns, rfDaily: 0.0, lag: mde.NwLag).AlphaAnnualized
                    : d.Average() * MetricsConstants.TradingDaysPerYear;

                var verdict = PromotionGate.Decide(effect, mde.MdeAnn, d.Length, gate.MinTrackDays);
                results.Add(new RecomputedVerdict(strategyId, asOf, effect, mde.MdeAnn, mde.TDays, verdict));
            }
        }

        return results;
    }

    /// <summary>The curve as it stood at the close of <paramref name="asOf"/> — ordinal on ISO-8601 dates,
    /// the house comparison convention.</summary>
    private static List<(string AsOf, decimal Equity)> Truncate(
        List<(string AsOf, decimal Equity)> curve, string asOf) =>
        curve.Where(p => string.CompareOrdinal(p.AsOf, asOf) <= 0).ToList();
}
