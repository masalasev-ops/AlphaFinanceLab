using System.Globalization;

namespace AlphaLab.Evaluation.Recompute;

/// <summary>
/// Which stored inputs a candidate rule change needs (MASTER §25.2, as amended by D117). The tier is
/// determined by which PARAMETERS the rule touches, never by inspecting its results — which is why the
/// harness takes a specification it can examine rather than a bare set of values.
/// </summary>
public enum RecomputeTier
{
    /// <summary>Threshold and sustain-count changes: pure over <c>overfitting_checks.value</c> +
    /// <c>contribution</c> (+ <c>threshold_json</c> for the inputs a signal recorded beside its value).</summary>
    DirectRead = 1,

    /// <summary>Needs member window alphas re-derived from <c>control_equity</c> — no simulation, but not a
    /// stored column. A band-definition change, AND (finding 340) any change to S6's negative-alpha
    /// threshold.</summary>
    DerivedBand = 2,

    /// <summary>Needs the paired return series re-derived from <c>equity_curve</c>: the alpha definition and
    /// the gate rules that consume it (finding 339 — §25.2's original table had no row for this, though its
    /// own prose listed "the alpha definition" as covered).</summary>
    EquityDerived = 3,
}

/// <summary>Thrown when the harness will not answer for a specification. Refusing is the specified
/// behaviour (§25.2): a harness that appears to cover a case and quietly returns a wrong answer is the
/// failure mode §25.4 calls worse than a slow correct one.</summary>
public sealed class RecomputeRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// The parameters a <see cref="RecomputeSpec"/> may override, each mapped to the tier of inputs its change
/// requires. A name absent from this map is REFUSED — the harness never guesses a tier.
/// </summary>
public static class RecomputeParameters
{
    // ---- monitor: S2 ----
    public const string S2ElevatedGapRawSharpe = "monitor.s2.elevated_gap_raw_sharpe";

    // ---- monitor: S3 ----
    public const string S3HealthyAnchor = "monitor.s3.healthy_anchor";
    public const string S3SuspectAnchor = "monitor.s3.suspect_anchor";
    public const string S3SustainEvals = "monitor.s3.sustain_evals";

    // ---- monitor: S6 ----
    public const string S6SustainEvals = "monitor.s6.sustain_evals";
    public const string S6NegativeAlphaT = "monitor.s6.negative_alpha_t";
    public const string S6BandLowPct = "monitor.s6.band_low_pct";
    public const string S6BandHighPct = "monitor.s6.band_high_pct";

    // ---- monitor: aggregate ----
    public const string AutoRetireEvals = "monitor.auto_retire_evals";

    // ---- gate ----
    /// <summary>`raw_gap` (generation 1's behaviour, finding 285's defect) | `jensen` (D26/rule 6).</summary>
    public const string AlphaDefinition = "gate.alpha_definition";

    public const string AlphaRawGap = "raw_gap";
    public const string AlphaJensen = "jensen";

    /// <summary>
    /// Parameter → the tier of inputs a change to it requires.
    ///
    /// **<see cref="S6NegativeAlphaT"/> is DerivedBand, not DirectRead, and the reason is finding 340.**
    /// §25.1 recorded that `insideCentralBand` is recoverable from the S6 contribution token. That holds
    /// only for rows that did NOT take the negative-alpha branch: <c>MonitorSignals.S6</c> returns EARLY on
    /// <c>rollingAlphaT &lt; S6NegativeAlphaT</c> and never evaluates band membership, so those rows record
    /// no band information at all. Move the threshold and exactly those rows fall through to a band check
    /// whose input was never stored — so the change needs `control_equity`, like a band-definition change.
    /// Classifying it DirectRead is the specific way a v1 harness would look correct and be wrong on the
    /// knob finding 280 most points at.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RecomputeTier> Tiers =
        new Dictionary<string, RecomputeTier>(StringComparer.Ordinal)
        {
            [S2ElevatedGapRawSharpe] = RecomputeTier.DirectRead,
            [S3HealthyAnchor] = RecomputeTier.DirectRead,
            [S3SuspectAnchor] = RecomputeTier.DirectRead,
            [S3SustainEvals] = RecomputeTier.DirectRead,
            [S6SustainEvals] = RecomputeTier.DirectRead,
            [AutoRetireEvals] = RecomputeTier.DirectRead,
            [S6NegativeAlphaT] = RecomputeTier.DerivedBand,   // finding 340 — see the remarks above
            [S6BandLowPct] = RecomputeTier.DerivedBand,
            [S6BandHighPct] = RecomputeTier.DerivedBand,
            [AlphaDefinition] = RecomputeTier.EquityDerived,
        };
}

/// <summary>
/// A candidate rule change, stated as parameter overrides the harness can EXAMINE (§25.2). An empty spec
/// is the parity case: recompute under the rules generation 1 actually ran, which is what
/// <c>FX-RecomputeParity</c> asserts before the harness is trusted for anything else.
/// </summary>
public sealed record RecomputeSpec(string Name, IReadOnlyDictionary<string, string> Overrides)
{
    /// <summary>The parity spec: no overrides, current rules.</summary>
    public static RecomputeSpec Parity { get; } =
        new("parity", new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>The tier of inputs this spec needs = the MAX over its parameters. Throws
    /// <see cref="RecomputeRefusedException"/> on any parameter the map does not know.</summary>
    public RecomputeTier Tier
    {
        get
        {
            var tier = RecomputeTier.DirectRead;
            foreach (var key in Overrides.Keys)
            {
                if (!RecomputeParameters.Tiers.TryGetValue(key, out var t))
                {
                    throw new RecomputeRefusedException(
                        $"Recompute refused: unknown parameter '{key}'. The harness classifies a change by " +
                        "which parameters it touches (§25.2) and never guesses a tier — an unclassifiable " +
                        "specification is refused rather than answered. Known parameters: " +
                        string.Join(", ", RecomputeParameters.Tiers.Keys.Order(StringComparer.Ordinal)) + ".");
                }
                if (t > tier) tier = t;
            }
            return tier;
        }
    }

    public double Double(string key, double fallback) =>
        Overrides.TryGetValue(key, out var v)
            ? double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)
            : fallback;

    public int Int(string key, int fallback) =>
        Overrides.TryGetValue(key, out var v)
            ? int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : fallback;

    public string Text(string key, string fallback) =>
        Overrides.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>A stable one-line rendering for the report header — the spec IS the experiment's identity,
    /// so it is recorded verbatim beside its result rather than summarised.</summary>
    public string Describe() =>
        Overrides.Count == 0
            ? $"{Name} (no overrides — generation 1's rules)"
            : $"{Name}: " + string.Join(", ",
                Overrides.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
}
