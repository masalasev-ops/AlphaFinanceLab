using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Numerics;
using AlphaLab.Evaluation.Signals;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>
/// The FR-46 Signal-Library read-model (D91, MASTER §24; UX-16). Descriptive only — nothing it produces
/// is an input to the allocator (D51), any gate, sizing, or eligibility.
///
/// IT ACCEPTS AN AS-OF, and that is a build-shape requirement rather than a convenience (finding 292).
/// The §24.6 Phase-5 digest puts one line per signal into a CONTEXT PACK, and D104's
/// <c>FX-PackNoLeak</c> binds every pack field: per-field, nothing carrying an <c>observed_at</c> later
/// than the simulated as-of; closure, whitelist-only. A read-model anchored to "now" could not serve
/// that without reopening this phase, so the seam is built in from the start:
///
///   • <c>asOf = null</c>  ⇒ the LIVE panel. Grades up to today, thresholds via <c>ResolveCurrent</c>.
///     A panel answers "is this signal decaying NOW", which is an operational read outside any run's
///     provenance — the <c>DetectabilityGate</c> precedent.
///   • <c>asOf = date</c>  ⇒ a PINNED read. Grades bounded by that date, thresholds via
///     <c>ResolveAsOf</c> (D96), so a pack assembled at that as-of cannot see a threshold version
///     recorded afterwards.
///
/// THE HONESTY RAILS ARE RESOLVED HERE, NOT IN A CHART (rule 18): a verdict the effective sample cannot
/// support ships as <c>insufficient</c> with its reason, never as a provisional "stable"; the effective
/// sample and both critical values travel with the verdict so a reader can recompute it; and the C-1
/// detection threshold rides along as reading context.
/// </summary>
public sealed class SignalLibraryBuilder(AlphaLabDbContext db, SignalLibraryOptions options)
{
    private const int SessionsPerYear = 252;

    /// <summary>The two D108 significance levels, as versioned config ROWS (never appsettings — they
    /// must be as-of resolvable to serve a pinned consumer).</summary>
    public const string DecayAlphaKey = "SignalLibrary.TrendDecayAlpha";
    public const string GoneAlphaKey = "SignalLibrary.TrendGoneAlpha";

    /// <summary>The detection power the published floors are quoted at (finding 305). A config ROW for
    /// the same reason the α values are: finding 292 needs it as-of resolvable, and appsettings is not.
    /// Deliberately NOT part of the FR-45 pin refusal — it governs a diagnostic, never a verdict, so an
    /// absent power must not block a 3-hour backfill.</summary>
    public const string MinDetectablePowerKey = "SignalLibrary.MinDetectablePower";

    /// <param name="asOf">null = the live panel; a date = a pinned read (finding 292).</param>
    public SignalLibraryReadModel Build(string? asOf = null)
    {
        var stamp = ReadModelStamps.LatestForward(db);
        var registry = db.Signals.OrderBy(s => s.SignalId).ToList();
        if (registry.Count == 0)
        {
            // No instruments registered yet: an empty panel stamped honestly, not a fabricated one.
            return new SignalLibraryReadModel { Stamp = stamp, AsOf = asOf, Signals = [] };
        }

        var (goneAlpha, decayAlpha, pinned) = ResolveThresholds(asOf);
        var power = ResolvePower(asOf);
        var flagWindowYears = options.ResolvedRollingWindowsYears.Max();   // finding 301

        var rows = new List<SignalPanelRow>();
        foreach (var signal in registry)
        {
            foreach (var horizon in options.ResolvedHorizonsDays)
            {
                // Grades are date-bounded by the as-of: a pinned read must not see a grade written for a
                // later day, which is the leakage FX-PackNoLeak exists to forbid.
                var series = db.SignalIc
                    .Where(r => r.SignalId == signal.SignalId && r.HorizonDays == horizon)
                    .Where(r => asOf == null || string.Compare(r.AsOf, asOf) <= 0)
                    .OrderBy(r => r.AsOf)
                    .Select(r => new { r.AsOf, r.RankIc })
                    .ToList();

                var windows = new List<SignalWindowGrade>();
                foreach (var years in options.ResolvedRollingWindowsYears.OrderBy(y => y))
                {
                    var take = years * SessionsPerYear;
                    var slice = series.Count <= take ? series : series.Skip(series.Count - take).ToList();
                    var ic = slice.Select(s => s.RankIc).ToList();
                    var sample = new EffectiveSample(Math.Min(slice.Count, take), horizon);

                    double? lo = null, hi = null;
                    if (ic.Count >= 2 && sample.CanInfer && pinned)
                    {
                        // Divisor is the NOMINAL count: σ²_LR already carries the overlap correction,
                        // and dividing by n_eff would double-count it (finding 306). n_eff still sets
                        // the df on the next line.
                        var se = Math.Sqrt(NeweyWest.LongRunVariance(ic, horizon) / Math.Max(1, ic.Count));
                        var crit = StudentT.TwoSidedCritical(goneAlpha, sample.LevelDf);
                        lo = ic.Average() - crit * se;
                        hi = ic.Average() + crit * se;
                    }
                    windows.Add(new SignalWindowGrade(
                        years, ic.Count > 0 ? ic.Average() : 0.0, lo, hi, ic.Count, sample.Count));
                }

                // The FLAG is inferred on the LONGEST window for BOTH horizons (D108).
                var flagTake = flagWindowYears * SessionsPerYear;
                var flagIc = (series.Count <= flagTake ? series : series.Skip(series.Count - flagTake).ToList())
                    .Select(s => s.RankIc).ToList();

                if (!pinned)
                {
                    // Thresholds unpinned ⇒ no verdict, and the reason says which absence caused it. The
                    // backfill refuses to write grades in this state at all, so this is the belt to that
                    // brace rather than the primary defence.
                    rows.Add(new SignalPanelRow(
                        signal.SignalId, signal.Family, horizon, signal.CodeVersion, windows,
                        TrendFlag.Insufficient, SignalPanelRow.ReasonNotPinned, null, null, null));
                    continue;
                }

                var verdict = SignalTrendInference.Infer(
                    flagIc, horizon, Math.Min(flagIc.Count, flagTake), goneAlpha, decayAlpha, power);

                rows.Add(new SignalPanelRow(
                    signal.SignalId, signal.Family, horizon, signal.CodeVersion, windows,
                    verdict.Flag,
                    verdict.Flag == TrendFlag.Insufficient ? SignalPanelRow.ReasonBelowEffectiveSampleFloor : null,
                    verdict.TStat, verdict.LevelCritical, verdict.TrendCritical,
                    verdict.MinDetectableIc, verdict.MinDetectableTrendPerYear,
                    verdict.TStat is null ? null : verdict.StdError, verdict.SlopeStdError,
                    DetectabilityReason(power, verdict)));
            }
        }

        return new SignalLibraryReadModel
        {
            Stamp = stamp,
            AsOf = asOf,
            Signals = rows,
            DetectionContext = DetectionContext(asOf),
        };
    }

    /// <summary>
    /// Resolve the pinned significance levels, as-of when a date is given (D96/finding 292) and current
    /// otherwise. <c>pinned</c> false means at least one row is absent — reported, never defaulted: a
    /// silently-defaulted significance level is precisely the "choosing thresholds by looking at the
    /// answer" that D108's pin-before-grade rule exists to prevent.
    /// </summary>
    /// <summary>
    /// Why the detectability floors are absent, when they are. Stated rather than left blank: a missing
    /// number beside a verdict reads as "nothing to report", and the whole point of finding 305 is that
    /// an absent floor is exactly what a reader must not silently accept.
    /// </summary>
    private static string? DetectabilityReason(double? power, SignalTrendInference.Verdict verdict)
    {
        if (verdict.MinDetectableIc is not null) return null;
        if (power is null) return SignalPanelRow.ReasonPowerNotPinned;
        return SignalPanelRow.ReasonBelowEffectiveSampleFloor;
    }

    /// <summary>The pinned detection power, resolved on the same as-of seam as the α values. Absent ⇒
    /// null ⇒ the floors are withheld with their reason, never quoted at an unchosen power.</summary>
    private double? ResolvePower(string? asOf)
    {
        var config = new ConfigReadService(db);
        var raw = asOf is null
            ? config.ResolveCurrent(MinDetectablePowerKey)
            : config.ResolveAsOf(MinDetectablePowerKey, asOf);
        if (raw is null) return null;
        return double.TryParse(raw.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
               && p > 0 && p < 1
            ? p
            : null;
    }

    private (double Gone, double Decay, bool Pinned) ResolveThresholds(string? asOf)
    {
        var config = new ConfigReadService(db);
        var goneRaw = asOf is null ? config.ResolveCurrent(GoneAlphaKey) : config.ResolveAsOf(GoneAlphaKey, asOf);
        var decayRaw = asOf is null ? config.ResolveCurrent(DecayAlphaKey) : config.ResolveAsOf(DecayAlphaKey, asOf);

        if (!TryAlpha(goneRaw, out var gone) || !TryAlpha(decayRaw, out var decay)) return (0, 0, false);
        return (gone, decay, true);
    }

    private static bool TryAlpha(string? raw, out double alpha)
    {
        alpha = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!double.TryParse(raw.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
        if (v is <= 0 or >= 1) return false;   // not a significance level — refuse rather than clamp
        alpha = v;
        return true;
    }

    /// <summary>
    /// The Phase-4 C-1 detection threshold, verbatim from the frozen <c>Calibration.DetectionPower</c>
    /// row, as READING CONTEXT beside the grades.
    ///
    /// Rank-IC and strategy detection are DIFFERENT QUANTITIES and nothing here converts between them —
    /// the first is a cross-sectional predictive correlation of a scoring rule with no costs, sizing,
    /// exits or account; the second is the end-to-end probability that a whole trading operation clears
    /// the promotion gate under costs. They belong side by side precisely because a signal can grade
    /// well and still describe an edge the arena could never confirm.
    /// </summary>
    private string? DetectionContext(string? asOf)
    {
        var config = new ConfigReadService(db);
        var raw = asOf is null
            ? config.ResolveCurrent("Calibration.DetectionPower")
            : config.ResolveAsOf("Calibration.DetectionPower", asOf);
        return raw is null
            ? null
            : "Detection context (Phase-4 C-1, finding 288): rank-IC measures a SCORING RULE; the arena's " +
              "measured detection threshold is a property of a WHOLE trading operation under costs. No " +
              "arithmetic converts one into the other — a signal can grade well and still describe an edge " +
              "the arena could never confirm. See docs/calibration/ for the per-rung curve.";
    }
}
