using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Numerics;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>
/// Builds the D41 factor-attribution panel (DESIGN_IMPROVEMENTS §1.4).
///
/// **EVERY HONESTY DECISION IS RESOLVED HERE, NOT IN THE CLIENT (rule 18).** The unavailable reason, the
/// formatting, the lag sentence and the coverage counts are all fields by the time this returns. A UI that
/// decides whether a decomposition is trustworthy is a bug.
///
/// **NO STATISTICS IN THE API (rule 17).** The regression runs in AlphaLab.Evaluation; `AlphaLab.Api`
/// calls `Build` and serializes the result.
///
/// **FORWARD-ONLY.** The panel reads the `live` run kind. Replay is quarantined from every forward view
/// (rule 1) and the attribution panel is a forward view, so it routes through `ForwardVisibility` rather
/// than re-expressing the predicate (D149).
/// </summary>
public sealed class AttributionReadModelBuilder(AlphaLabDbContext db, GateOptions gate)
{
    private const string RunKindLive = "live";

    /// <summary>§1.4's regressor set, in its stated order. CMA is listed as OPTIONAL there and is
    /// deliberately excluded: adding a sixth regressor is a modelling choice, and the spec's parenthetical
    /// is not an instruction to make it.</summary>
    public static readonly IReadOnlyList<string> Regressors = ["MKT_RF", "SMB", "HML", "UMD", "RMW"];

    /// <summary>
    /// The minimum track §1.4 asks for — *"per strategy (≥ ~1y of track)"* — read as one trading year.
    /// **DERIVED, NOT AUTHORED (finding 309):** it is `MetricsConstants.TradingDaysPerYear`, the same 252
    /// every annualization in the system already uses, rather than a fresh constant that happens to equal
    /// it. The "~" in the spec is why this is a floor rather than an exact match.
    /// </summary>
    public static int MinimumTrackDays => (int)MetricsConstants.TradingDaysPerYear;

    public AttributionReadModel Build(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        var stamp = ReadModelStamps.LatestForward(db);
        if (stamp.Status == ReadModelStampStatus.NoRunYet) return AttributionReadModel.NoRunYet;

        // D149: forward visibility is ONE seam. A replay fixture must never render as a forward card.
        var visible = ForwardVisibility.ForwardStrategies(db.Strategies)
            .Any(s => s.StrategyId == strategyId);
        if (!visible) return AttributionReadModel.NoRunYet;

        var through = db.FactorReturns.Max(f => (string?)f.Date);
        var lagNote = through is null ? null : $"factor data through {through}";

        AttributionReadModel Unavailable(string reason) => new()
        {
            Stamp = stamp,
            StrategyId = strategyId,
            HasFit = false,
            Unavailable = reason,
            FactorDataThrough = through,
            LagNote = lagNote,
        };

        if (through is null) return Unavailable(AttributionReadModel.UnavailableNoFactorData);

        var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strategyId && a.RunKind == RunKindLive);
        if (account is null) return Unavailable(AttributionReadModel.UnavailableInsufficientTrack);

        var curve = CurveMath.Curve(db, account.AccountId, RunKindLive);
        if (curve.Count < 2) return Unavailable(AttributionReadModel.UnavailableInsufficientTrack);

        var returns = new List<double>(curve.Count - 1);
        var dates = new List<string>(curve.Count - 1);
        for (var i = 1; i < curve.Count; i++)
        {
            var prev = curve[i - 1].Equity;
            if (prev <= 0m) continue;
            returns.Add((double)(curve[i].Equity / prev) - 1.0);
            dates.Add(curve[i].AsOf);
        }

        if (returns.Count < MinimumTrackDays) return Unavailable(AttributionReadModel.UnavailableInsufficientTrack);

        // The factor panel for exactly these sessions. A session missing ANY regressor is dropped whole:
        // fitting a row against a partially-present factor vector would silently treat a missing loading
        // as zero exposure, which is a claim about the strategy rather than about the data.
        var panel = LoadPanel(dates);
        var rows = new List<int>(dates.Count);
        for (var i = 0; i < dates.Count; i++)
        {
            if (panel.Rf[i] is null) continue;
            var complete = true;
            foreach (var f in Regressors)
            {
                if (panel.Factors[f][i] is null) { complete = false; break; }
            }
            if (complete) rows.Add(i);
        }

        var covered = rows.Count;
        if (covered < MinimumTrackDays) return Unavailable(AttributionReadModel.UnavailableFactorDataGap);

        // y = r_s − r_f, the §1.4 left-hand side. The factors are already excess by construction:
        // Mkt−RF is published as an excess return, and SMB/HML/UMD/RMW are long-short spreads.
        var y = new double[covered];
        var xs = new List<IReadOnlyList<double>>(Regressors.Count);
        for (var i = 0; i < covered; i++) y[i] = returns[rows[i]] - panel.Rf[rows[i]]!.Value;
        foreach (var f in Regressors)
        {
            var col = new double[covered];
            for (var i = 0; i < covered; i++) col[i] = panel.Factors[f][rows[i]]!.Value;
            xs.Add(col);
        }

        // The lag is the config cap, DERIVED rather than authored: §1.4 states Newey–West errors and no
        // bandwidth, and the only rule in the corpus — the gate's min(2·maxHorizon, NwLagCapDays) — is
        // written for a horizon-keyed comparison. Attribution has no horizon, so that rule degenerates to
        // its cap. See HacOls' remarks.
        HacOlsFit fit;
        try
        {
            fit = HacOls.Fit(y, xs, gate.NwLagCapDays);
        }
        catch (ArgumentException)
        {
            // A rank-deficient factor panel (a constant or collinear column over this window). Refuse
            // rather than report loadings nobody can interpret — the HacOls posture, surfaced as a reason.
            return Unavailable(AttributionReadModel.UnavailableDegenerateDesign);
        }

        var loadings = new List<FactorLoading>(Regressors.Count);
        for (var j = 0; j < Regressors.Count; j++)
        {
            loadings.Add(new FactorLoading(
                Regressors[j], fit.Betas[j], fit.BetaSes[j], fit.BetaT(j),
                fit.Betas[j].ToString("F2", CultureInfo.InvariantCulture)));
        }

        return new AttributionReadModel
        {
            Stamp = stamp,
            StrategyId = strategyId,
            HasFit = true,
            AlphaAnnualized = fit.Alpha * MetricsConstants.TradingDaysPerYear,
            AlphaFormatted = (fit.Alpha * MetricsConstants.TradingDaysPerYear * 100.0)
                .ToString("F2", CultureInfo.InvariantCulture) + " %/yr",
            AlphaTStat = fit.AlphaT,
            Loadings = loadings,
            N = fit.N,
            Lag = fit.Lag,
            FactorDataThrough = through,
            LagNote = lagNote,
            CoveredSessions = covered,
            TotalSessions = dates.Count,
        };
    }

    private sealed record Panel(IReadOnlyList<double?> Rf, IReadOnlyDictionary<string, IReadOnlyList<double?>> Factors);

    /// <summary>One query for every factor over the window, pivoted to positional arrays. Null marks an
    /// absent observation — deliberately nullable rather than 0.0, because a zero-valued factor day and a
    /// missing one are different and only one of them may enter a fit.</summary>
    private Panel LoadPanel(IReadOnlyList<string> dates)
    {
        var wanted = dates.ToHashSet(StringComparer.Ordinal);
        var needed = new List<string>(Regressors) { RiskFreeSeries.RfFactor };

        var raw = db.FactorReturns
            .Where(f => needed.Contains(f.Factor))
            .Select(f => new { f.Date, f.Factor, f.Value })
            .AsEnumerable()
            .Where(f => wanted.Contains(f.Date))
            .ToLookup(f => f.Factor, f => (f.Date, f.Value));

        IReadOnlyList<double?> Column(string factor)
        {
            var byDate = raw[factor].ToDictionary(x => x.Date, x => x.Value, StringComparer.Ordinal);
            var col = new double?[dates.Count];
            for (var i = 0; i < dates.Count; i++)
            {
                col[i] = byDate.TryGetValue(dates[i], out var v) ? v : null;
            }
            return col;
        }

        var factors = new Dictionary<string, IReadOnlyList<double?>>(StringComparer.Ordinal);
        foreach (var f in Regressors) factors[f] = Column(f);
        return new Panel(Column(RiskFreeSeries.RfFactor), factors);
    }
}
