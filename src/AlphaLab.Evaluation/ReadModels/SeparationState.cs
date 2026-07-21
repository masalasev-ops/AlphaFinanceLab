using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>
/// The D63/FR-35 separation state, computed from a strategy's persisted S3 percentile path vs the
/// Verdicts.* config (MASTER §20.8). Reconstructible from the overfitting_checks signal='S3' rows (NFR-2).
/// It is NOT a monitor status and carries NO veto or allocation consequence — it renders beside the gate
/// verdict. Once the track reaches SeparationMinTrackDays, a persistent 'none' surfaces the
/// IndistinguishableFromRandom chip with its day count.
/// </summary>
public static class SeparationState
{
    public static SeparationInfo Resolve(AlphaLabDbContext db, string strategyId, VerdictsOptions verdicts, string runKind)
    {
        var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strategyId && a.RunKind == runKind);
        var days = account is null ? 0 : db.EquityCurve.Count(e => e.AccountId == account.AccountId && e.RunKind == runKind);

        var min = verdicts.SeparationMinTrackDays;

        var s3Path = db.OverfittingChecks
            .Where(c => c.StrategyId == strategyId && c.Signal == "S3" && c.RunKind == runKind && c.Value != null)
            .OrderBy(c => c.AsOf).ThenBy(c => c.CheckId)
            .Select(c => c.Value!.Value)
            .ToList();

        if (s3Path.Count == 0) return new SeparationInfo(SeparationInfo.None, days, min);

        var latest = s3Path[^1];

        // The population's central band: SeparationBandCentralFrac (0.50) ⇒ the 25th–75th pct region.
        var half = verdicts.SeparationBandCentralFrac / 2.0 * 100.0;
        var lo = 50.0 - half;
        var hi = 50.0 + half;

        // A decisive gate verdict (anything but TooEarly) means the pair IS distinguishable (up or down).
        // Read the LATEST verdict — never an all-history .Any() — so a strategy that earned a decisive
        // verdict once but has since decayed back inside the MDE (latest verdict TooEarly) correctly reverts
        // to 'none' and surfaces the IndistinguishableFromRandom chip (D63/FR-35). This mirrors how the
        // Strategies builder resolves its gate verdict (latest by AsOf, then ReportId), so the tier and the
        // separation state can never disagree about the same strategy.
        var latestVerdict = db.PowerReports
            .Where(p => p.StrategyA == strategyId && p.RunKind == runKind)
            .OrderByDescending(p => p.AsOf).ThenByDescending(p => p.ReportId)
            .Select(p => p.Verdict)
            .FirstOrDefault();
        var decisive = latestVerdict is "Promoted" or "Refused";

        string state;
        if (decisive || latest >= 95.0) state = SeparationInfo.Distinguishable;   // sustained above P_edge (flat anchor)
        else if (latest < lo || latest > hi) state = SeparationInfo.Emerging;      // outside the central band
        else state = SeparationInfo.None;                                          // inside the band — not yet distinguishable

        return new SeparationInfo(state, days, min);
    }
}
