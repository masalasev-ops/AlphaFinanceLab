using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlphaLab.Evaluation.Calibration;

/// <summary>One knot of a piecewise-linear percentile curve over track-length days.</summary>
public sealed record CurveKnot(
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("p")] double P);

/// <summary>The archived 25–75% band at one knot (D64: curves ship with their seed dispersion).</summary>
public sealed record BandKnot(
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("lo")] double Lo,
    [property: JsonPropertyName("hi")] double Hi);

/// <summary>The curves' provenance stamp (D64 vintage; filled by the calibration job, 4.8).</summary>
public sealed record CurveVintage(
    [property: JsonPropertyName("arena")] string Arena,
    [property: JsonPropertyName("window_from")] string WindowFrom,
    [property: JsonPropertyName("window_to")] string WindowTo,
    [property: JsonPropertyName("watermark")] string Watermark,
    [property: JsonPropertyName("membership_source")] string MembershipSource,
    [property: JsonPropertyName("plant")] string Plant,
    [property: JsonPropertyName("seeds")] int Seeds,
    [property: JsonPropertyName("population_m")] int PopulationM,
    [property: JsonPropertyName("learn_through")] string? LearnThrough,
    [property: JsonPropertyName("survivorship_caveat")] string SurvivorshipCaveat,
    [property: JsonPropertyName("slice_caveat")] string SliceCaveat);

/// <summary>
/// A calibrated D56 S3 trajectory curve — P_noise(t) or P_edge(t) — stored as a VERSIONED config row
/// (D98; finding 108 is what makes that implementable): piecewise-linear knots on the evaluation-cadence
/// grid, linear interpolation between, FLAT extrapolation beyond both ends. Carries the 25–75% seed band,
/// the C-2 sampling band (the percentile cut rides an M-member empirical distribution, so it carries
/// ~sqrt(M·q·(1−q)) members of binomial noise — archived so a future "should M be 500?" question has its
/// evidence), the sustain requirement, and the data vintage.
/// </summary>
public sealed record S3Curve(
    [property: JsonPropertyName("kind")] string Kind,                     // "p_noise" | "p_edge"
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("interp")] string Interp,                 // "piecewise_linear"
    [property: JsonPropertyName("sustain_evals")] int SustainEvals,
    [property: JsonPropertyName("false_alarm_rate")] double FalseAlarmRate,
    [property: JsonPropertyName("knots")] IReadOnlyList<CurveKnot> Knots,
    [property: JsonPropertyName("band_25_75")] IReadOnlyList<BandKnot> Band2575,
    [property: JsonPropertyName("sampling_band_members")] double SamplingBandMembers,
    [property: JsonPropertyName("vintage")] CurveVintage? Vintage)
{
    /// <summary>The curve value at a track length: linear between knots, flat beyond the ends.</summary>
    public double At(int trackDays)
    {
        if (Knots.Count == 0) throw new InvalidOperationException($"S3 {Kind} curve has no knots (fail closed).");
        if (trackDays <= Knots[0].T) return Knots[0].P;
        if (trackDays >= Knots[^1].T) return Knots[^1].P;
        for (var i = 1; i < Knots.Count; i++)
        {
            if (trackDays > Knots[i].T) continue;
            var (a, b) = (Knots[i - 1], Knots[i]);
            var w = (trackDays - a.T) / (double)(b.T - a.T);
            return a.P + w * (b.P - a.P);
        }
        return Knots[^1].P;
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static S3Curve FromJson(string json) =>
        JsonSerializer.Deserialize<S3Curve>(json)
        ?? throw new InvalidOperationException("config row does not deserialize to an S3Curve (fail closed).");
}

/// <summary>The D98 config-row key names for the calibrated values (dotted-PascalCase, the
/// Regime.ProxySecurityId precedent; family-suffixed where curves are per family).</summary>
public static class CalibratedKeys
{
    public static string PNoiseCurve(string family) => $"Monitor.S3.PNoiseCurve.{family}";
    public static string PEdgeCurve(string family) => $"Monitor.S3.PEdgeCurve.{family}";

    /// <summary>The calibrated S6 auto-retire patience (integer evaluations; finding 113: a survival-floor
    /// failure recalibrates THIS, never the plant).</summary>
    public const string S6AutoRetireEvals = "Monitor.S6.AutoRetireEvals";

    /// <summary>The C-1 empirical detection-power curves — the FR-40 gate's empirical floor.</summary>
    public const string DetectionPower = "Calibration.DetectionPower";

    /// <summary>{path, sha256} of the archived calibration report (DB ↔ report cross-reference).</summary>
    public const string ReportRef = "Calibration.ReportRef";
}
