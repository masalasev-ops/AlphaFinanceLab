using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Monitor;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// D98/D56 (checkpoint 4.6): the calibrated-curve model (interpolation, JSON round-trip), the curve
/// builder's statistics, and the S3 trajectory judgment (FX-S3Trajectory at the signal level: the
/// anti-predictive path escalates to Suspect, the no-edge path sits "between" and never trips Suspect,
/// the edge path reads above the curve).
/// </summary>
public class CalibratedCurveTests
{
    private static S3Curve Curve(string kind, params (int T, double P)[] knots) => new(
        kind, "daily", "piecewise_linear", SustainEvals: 2, FalseAlarmRate: 0.05,
        knots.Select(k => new CurveKnot(k.T, k.P)).ToList(), [], SamplingBandMembers: 1.5, Vintage: null);

    [Fact]
    public void S3Curve_At_InterpolatesLinearly_ExtrapolatesFlat()
    {
        var curve = Curve("p_edge", (21, 50.0), (41, 60.0), (85, 70.0));
        Assert.Equal(50.0, curve.At(10));    // before the first knot: flat
        Assert.Equal(50.0, curve.At(21));
        Assert.Equal(55.0, curve.At(31), 6); // midpoint of (21,50)-(41,60)
        Assert.Equal(60.0, curve.At(41));
        Assert.Equal(65.0, curve.At(63), 6); // midpoint of (41,60)-(85,70)
        Assert.Equal(70.0, curve.At(85));
        Assert.Equal(70.0, curve.At(500));   // beyond the last knot: flat
    }

    [Fact]
    public void S3Curve_JsonRoundTrip_PreservesEverything()
    {
        var curve = new S3Curve("p_noise", "daily", "piecewise_linear", 2, 0.05,
            [new CurveKnot(21, 12.3), new CurveKnot(42, 14.5)],
            [new BandKnot(21, 8.0, 18.0)],
            1.5,
            new CurveVintage("sp500", "2010-01-04", "2025-06-30", "2026-07-22T14:00:00Z",
                "fja05680 sha256:abc", "realistic", 50, 200, "2020-12-31",
                "pre-launch data carries residual survivorship bias (MASTER §13.4)",
                "curves calibrated on S&P 500 as-of membership; the forward universe is the S&P 100 slice until the widen"));

        var roundTripped = S3Curve.FromJson(curve.ToJson());
        Assert.Equal(curve.ToJson(), roundTripped.ToJson());   // byte-identical rendering (lists are by-value in JSON)
        Assert.Equal(curve.Vintage, roundTripped.Vintage);
        Assert.Equal(12.3, roundTripped.At(21));
        Assert.Equal(2, roundTripped.SustainEvals);
        Assert.Equal(8.0, roundTripped.Band2575[0].Lo);
    }

    [Fact]
    public void CurveBuilder_MedianAndQuantile_OnHandComputedPaths()
    {
        // Five seed paths, two evaluations each — hand-checkable.
        List<List<double>> edge = [[40, 60], [50, 70], [60, 80], [45, 65], [55, 75]];
        var pEdge = CurveBuilder.BuildEdge(edge, "daily", evalCadenceDays: 21, sustainEvals: 2,
            falseAlarmRate: 0.05, populationM: 200, vintage: null);
        Assert.Equal(2, pEdge.Knots.Count);
        Assert.Equal(21, pEdge.Knots[0].T);
        Assert.Equal(50.0, pEdge.Knots[0].P);   // median of 40,45,50,55,60
        Assert.Equal(42, pEdge.Knots[1].T);
        Assert.Equal(70.0, pEdge.Knots[1].P);
        Assert.Equal(45.0, pEdge.Band2575[0].Lo);
        Assert.Equal(55.0, pEdge.Band2575[0].Hi);

        // P_noise at the 5% quantile of the no-edge distribution sits near its floor.
        List<List<double>> noEdge = [[30], [40], [50], [60], [70]];
        var pNoise = CurveBuilder.BuildNoise(noEdge, "daily", 21, 2, 0.05, 200, null);
        Assert.Single(pNoise.Knots);
        Assert.True(pNoise.Knots[0].P <= 35.0);
        Assert.True(pNoise.SamplingBandMembers > 0);
    }

    [Fact]
    public void CurveBuilder_UnevenPathLengths_UseWhatExistsPerIndex()
    {
        List<List<double>> paths = [[40, 60, 80], [50], [60, 70]];
        var curve = CurveBuilder.BuildEdge(paths, "daily", 21, 2, 0.05, 200, null);
        Assert.Equal(3, curve.Knots.Count);
        Assert.Equal(50.0, curve.Knots[0].P);   // median of 40,50,60
        Assert.Equal(65.0, curve.Knots[1].P);   // median of 60,70
        Assert.Equal(80.0, curve.Knots[2].P);   // the lone survivor
    }

    // ---- FX-S3Trajectory at the signal level (the calibrated judgment, D56/D63) ----

    [Fact]
    public void FX_S3Trajectory_AntiPlantPath_EscalatesToSuspect()
    {
        var noise = 20.0;
        var edge = 60.0;
        // First evaluation below P_noise: WARNING (sustain not yet met) — a single dip never kills.
        var first = MonitorSignals.S3Trajectory(15.0, 21, noise, edge, priorConsecutiveBelowNoise: 0, sustainEvals: 2);
        Assert.Equal(MonitorStatus.Warning, first.Status);
        Assert.Equal("below_noise", first.Contribution);
        // Second consecutive below: SUSPECT — the anti-predictive fast-kill channel (D63).
        var second = MonitorSignals.S3Trajectory(12.0, 42, noise, edge, priorConsecutiveBelowNoise: 1, sustainEvals: 2);
        Assert.Equal(MonitorStatus.Suspect, second.Status);
        Assert.Equal("suspect", second.Contribution);
    }

    [Fact]
    public void FX_S3Trajectory_NoEdgePlant_SitsBetween_NeverSuspect()
    {
        // Mid-band forever: "between" (Warning per D56's stated bands), NEVER Suspect — its
        // indistinguishability is the D63 separation state's job, not a monitor kill.
        for (var evaluation = 0; evaluation < 20; evaluation++)
        {
            var s = MonitorSignals.S3Trajectory(50.0, (evaluation + 1) * 21, 20.0, 90.0, 0, 2);
            Assert.Equal("between", s.Contribution);
            Assert.Equal(MonitorStatus.Warning, s.Status);
        }
    }

    [Fact]
    public void FX_S3Trajectory_EdgePlant_ReadsAboveTheCurve()
    {
        var s = MonitorSignals.S3Trajectory(95.0, 84, 20.0, 90.0, 0, 2);
        Assert.Equal("above_edge", s.Contribution);
        Assert.Equal(MonitorStatus.Healthy, s.Status);
        // And it is NEVER Suspect at any horizon while above the noise envelope (≥ Warning, FX text).
        Assert.NotEqual(MonitorStatus.Suspect, MonitorSignals.S3Trajectory(40.0, 84, 20.0, 90.0, 0, 2).Status);
    }

    [Fact]
    public void S3_StreakTokens_ContinueAndBreakCorrectly()
    {
        Assert.True(MonitorSignals.ContinuesBelowNoiseStreak("below_noise"));
        Assert.True(MonitorSignals.ContinuesBelowNoiseStreak("suspect"));
        Assert.False(MonitorSignals.ContinuesBelowNoiseStreak("between"));
        Assert.True(MonitorSignals.ContinuesInsideBandStreak("inband"));
        Assert.True(MonitorSignals.ContinuesInsideBandStreak("elevated_inband"));
        Assert.False(MonitorSignals.ContinuesInsideBandStreak("none"));
        Assert.True(MonitorSignals.ContinuesNegativeTStreak("elevated_neg_alpha"));
        Assert.False(MonitorSignals.ContinuesNegativeTStreak("inband"));
    }
}
