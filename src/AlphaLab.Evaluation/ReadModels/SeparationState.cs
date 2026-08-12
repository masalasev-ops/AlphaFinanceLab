using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Evaluation.Monitor;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>
/// The D63/FR-35 separation state, computed from a strategy's persisted S3 percentile path vs the
/// Verdicts.* config (MASTER §20.8). Reconstructible from the overfitting_checks signal='S3' rows (NFR-2).
/// It is NOT a monitor status and carries NO veto or allocation consequence — it renders beside the gate
/// verdict. Once the track reaches SeparationMinTrackDays, a persistent 'none' surfaces the
/// IndistinguishableFromRandom chip with its day count.
///
/// §20.8 VERBATIM, because all three arms were implemented as single-point tests against a hardcoded
/// anchor and every clause below was violated (D148):
///   distinguishable — "the percentile path has been SUSTAINED above P_edge(t) (mirroring S3 Healthy),
///                      or a gate verdict other than TooEarly has been earned"
///   emerging        — "the path is SUSTAINED outside the population's central band"
///   none            — "the path remains inside the central band"
///
/// THE BAR IS READ, NEVER RESTATED. `P_edge(t)` is a calibrated curve that varies with track length —
/// at sp500 generation 2 it runs 71.0 at t=252 rising to ~83 — so the literal `95.0` this used was not
/// a rounding of it but a different rule entirely, and a CONSERVATIVE one: the doc-conformant bar admits
/// more paths, not fewer. That is why fixing this arm alone would have raised the false-positive rate,
/// and why the sustain requirement had to land in the same change.
///
/// It is taken from the check row the monitor WROTE (`threshold_json.p_edge_at`, or `healthy_anchor` on
/// the pre-calibration fallback) rather than re-resolved from config. The monitor and this read-model
/// then cannot disagree about the same strategy on the same day, which they previously could and did:
/// the monitor read the frozen `Monitor.S3.PEdgeCurve.{family}` while this compared against 95.
///
/// "MIRRORING S3 HEALTHY" also fixes WHICH sustain applies — the monitor's own, carried on the same row
/// (`sustain_evals`), falling back to <see cref="MonitorSignals.FlatAnchorSustainEvals"/> pre-calibration.
/// §20.8 defines no separate parameter, deliberately: a second knob could drift from the signal it is
/// defined to mirror.
/// </summary>
public static class SeparationState
{
    public static SeparationInfo Resolve(AlphaLabDbContext db, string strategyId, VerdictsOptions verdicts, string runKind)
    {
        var account = db.Accounts.FirstOrDefault(a => a.StrategyId == strategyId && a.RunKind == runKind);
        var days = account is null ? 0 : db.EquityCurve.Count(e => e.AccountId == account.AccountId && e.RunKind == runKind);

        var min = verdicts.SeparationMinTrackDays;

        // The path AND the bars the monitor judged each point against, oldest → newest.
        var path = db.OverfittingChecks
            .Where(c => c.StrategyId == strategyId && c.Signal == "S3" && c.RunKind == runKind && c.Value != null)
            .OrderBy(c => c.AsOf).ThenBy(c => c.CheckId)
            .Select(c => new { Percentile = c.Value!.Value, c.ThresholdJson })
            .ToList();

        if (path.Count == 0) return new SeparationInfo(SeparationInfo.None, days, min);

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
        //
        // D146 fixed the OTHER half of this clause: a degenerate pair (never traded ⇒ gap 0, MDE 0) used to
        // earn `Refused` here and lose its chip. This arm is unchanged — it was always right that a decisive
        // verdict means distinguishable; what was wrong was which pairs the gate called decisive.
        var latestVerdict = db.PowerReports
            .Where(p => p.StrategyA == strategyId && p.RunKind == runKind)
            .OrderByDescending(p => p.AsOf).ThenByDescending(p => p.ReportId)
            .Select(p => p.Verdict)
            .FirstOrDefault();
        var decisive = latestVerdict is "Promoted" or "Refused";

        var sustain = SustainEvals(path[^1].ThresholdJson);

        // SUSTAINED = the last `sustain` evaluations, this one included, all satisfy the arm. Fewer points
        // than that ⇒ nothing is sustained yet, so the state stays 'none' — the conservative direction, and
        // the one that keeps the chip rather than withholding it on thin evidence.
        var tail = path.Skip(Math.Max(0, path.Count - sustain)).ToList();
        var sustainedAboveEdge = tail.Count == sustain
            && tail.All(p => EdgeBar(p.ThresholdJson) is { } bar && p.Percentile >= bar);
        var sustainedOutsideBand = tail.Count == sustain
            && tail.All(p => p.Percentile < lo || p.Percentile > hi);

        string state;
        if (decisive || sustainedAboveEdge) state = SeparationInfo.Distinguishable;
        else if (sustainedOutsideBand) state = SeparationInfo.Emerging;
        else state = SeparationInfo.None;

        return new SeparationInfo(state, days, min);
    }

    /// <summary>The edge bar the monitor judged this evaluation against: the calibrated `p_edge_at` when
    /// the D56 curves were frozen, else the flat pre-calibration `healthy_anchor`. Null when the row
    /// carries neither — a shape this build does not recognise, which cannot satisfy the arm rather than
    /// falling back to a literal nobody chose.</summary>
    private static double? EdgeBar(string? thresholdJson) =>
        ReadNumber(thresholdJson, "p_edge_at") ?? ReadNumber(thresholdJson, "healthy_anchor");

    /// <summary>The monitor's own sustain for this evaluation (`sustain_evals`, written on the trajectory
    /// form), else the flat-anchor default. §20.8 says "mirroring S3 Healthy", so this is deliberately not
    /// a separate knob.</summary>
    private static int SustainEvals(string? thresholdJson)
    {
        var persisted = ReadNumber(thresholdJson, "sustain_evals");
        return persisted is { } v && v >= 1 ? (int)v : MonitorSignals.FlatAnchorSustainEvals;
    }

    private static double? ReadNumber(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble()
                : null;
        }
        catch (JsonException)
        {
            // A malformed threshold row cannot decide a rendered state. Treated as "no bar", which fails
            // toward 'none' — the chip — rather than toward a claim of separation.
            return null;
        }
    }
}
