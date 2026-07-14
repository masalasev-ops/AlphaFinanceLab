using AlphaLab.Data.Entities;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// FX-QualityGate (TEST_PLAN §2) — FR-6 data-quality gate. The gate is a PURE evaluator over a
/// security's candidate bars: it flags gaps, NaN/non-positive fields, robust-z return outliers, and
/// the dividend/split reconciliation alarm (an adj_close/close factor step with no matching event).
/// The external Alpaca cross-check is a dormant seam (no Alpaca account at launch); the reconciliation
/// half is fully built and tested here.
/// </summary>
public class DataQualityGateTests
{
    private static readonly DataQualityGate Gate = new(new DataQualityOptions()); // OutlierZ = 8.0 (CONFIG default)

    private static EodBar B(string date, double close, double factor) =>
        new(date, null, null, null, close, close * factor, 1000);

    private static CorporateActionRow Dividend(string exDate) => new()
    {
        SecurityId = 1,
        Type = "dividend",
        ExDate = exDate,
        EffectiveDate = exDate,
        CashPerShare = 0.25m,
        ObservedAt = "2026-03-21T00:00:00Z",
        Source = "eodhd"
    };

    // The canonical fixture: one gap day, one NaN close, one 12σ-scale outlier, one factor step
    // explained by a dividend (clean), and one factor step with no event (the reconciliation alarm).
    // Constructed so each anomaly contributes EXACTLY one flag and none cross-contaminates another.
    [Fact]
    public void FX_QualityGate_FlagsGapNanOutlierAndReconciliationAlarm()
    {
        // Raw closes vary realistically (~±0.5%/day, so the MAD scale is healthy); factor steps
        // 0.97→0.98 on the 03-05 dividend and 0.98→0.99 on 03-19 with NO event. A one-day level jump
        // to 139 on 03-10 is the return outlier (its adj scales by the same factor, so it does NOT read
        // as a factor step). The ~1.4% factor-step returns sit well inside the outlier cutoff.
        var bars = new List<EodBar>
        {
            B("2026-03-02", 100.00, 0.97),
            B("2026-03-03", 100.60, 0.97),
            B("2026-03-04", 100.10, 0.97),
            B("2026-03-05", 100.50, 0.98),                                  // dividend ex-date: factor steps up
            new("2026-03-06", null, null, null, double.NaN, double.NaN, 1000), // NaN close (present but unusable)
            B("2026-03-09", 101.20, 0.98),
            B("2026-03-10", 139.00, 0.98),                                  // outlier: +37% one-day move
            B("2026-03-11", 138.30, 0.98),
            B("2026-03-12", 139.00, 0.98),
            B("2026-03-13", 138.50, 0.98),
            B("2026-03-16", 139.20, 0.98),
            // 2026-03-17 omitted -> gap (it is in expectedDates below)
            B("2026-03-18", 138.70, 0.98),
            B("2026-03-19", 139.30, 0.99),                                  // unexplained factor step -> alarm
            B("2026-03-20", 138.90, 0.99),
        };
        string[] expected =
        [
            "2026-03-02","2026-03-03","2026-03-04","2026-03-05","2026-03-06","2026-03-09","2026-03-10",
            "2026-03-11","2026-03-12","2026-03-13","2026-03-16","2026-03-17","2026-03-18","2026-03-19","2026-03-20"
        ];

        var report = Gate.Evaluate("AAPL", bars, [Dividend("2026-03-05")], expected);

        // Exactly the four anomalies, each once.
        Assert.Equal(4, report.Flags.Count);
        Assert.Single(report.Flags, f => f.Issue == QualityIssue.MissingBar && f.Date == "2026-03-17");
        Assert.Single(report.Flags, f => f.Issue == QualityIssue.NanField && f.Date == "2026-03-06");
        Assert.Single(report.Flags, f => f.Issue == QualityIssue.OutlierReturn && f.Date == "2026-03-10");
        Assert.Single(report.Flags, f => f.Issue == QualityIssue.UnexplainedAdjustment && f.Date == "2026-03-19");

        // The dividend-explained factor step (03-05) is NOT flagged as a reconciliation alarm.
        Assert.DoesNotContain(report.Flags,
            f => f.Issue == QualityIssue.UnexplainedAdjustment && f.Date == "2026-03-05");

        // The NaN close is a fail-closed reject (rule 10); the gap/outlier/reconciliation flags are
        // warnings — HasRejects is driven ONLY by the field-integrity reject, never by an alarm.
        Assert.True(report.HasRejects);
        Assert.Equal(QualitySeverity.Reject,
            report.Flags.Single(f => f.Issue == QualityIssue.NanField).Severity);
        Assert.Equal(QualitySeverity.Warn, report.Flags.Single(f => f.Issue == QualityIssue.MissingBar).Severity);
        Assert.Equal(QualitySeverity.Warn, report.Flags.Single(f => f.Issue == QualityIssue.OutlierReturn).Severity);
        Assert.Equal(QualitySeverity.Warn, report.Flags.Single(f => f.Issue == QualityIssue.UnexplainedAdjustment).Severity);

        // Bar order must not matter — the gate sorts internally; shuffled input yields the same flags.
        var shuffled = bars.AsEnumerable().Reverse().ToList();
        var shuffledReport = Gate.Evaluate("AAPL", shuffled, [Dividend("2026-03-05")], expected);
        Assert.Equal(
            report.Flags.Select(f => (f.Issue, f.Date)).OrderBy(x => x.Date).ToList(),
            shuffledReport.Flags.Select(f => (f.Issue, f.Date)).OrderBy(x => x.Date).ToList());
    }

    // A PRESENT-but-corrupt adjusted close (NaN/inf/<=0) is the field every downstream return prices on,
    // so it fails closed (rule 10) even when the raw close is valid — it must never be silently defaulted.
    [Theory]
    [InlineData(double.NaN, QualityIssue.NanField)]
    [InlineData(double.PositiveInfinity, QualityIssue.NanField)]
    [InlineData(0.0, QualityIssue.NonPositivePrice)]
    [InlineData(-5.0, QualityIssue.NonPositivePrice)]
    public void CorruptAdjustedClose_WithValidRawClose_IsFailClosedReject(double badAdj, QualityIssue expected)
    {
        var bars = new List<EodBar> { new("2026-03-05", null, null, null, 100.50, badAdj, 1000) };
        var report = Gate.Evaluate("XYZ", bars, []);
        Assert.Single(report.Flags, f => f.Issue == expected && f.Severity == QualitySeverity.Reject && f.Date == "2026-03-05");
        Assert.True(report.HasRejects);
    }

    // A genuinely ABSENT (null) adjusted close on one bar of an adjusted series is benign — it must NOT
    // reject, and must NOT manufacture outliers by pricing that one return on the raw close (mixing bases).
    [Fact]
    public void NullAdjustedClose_OnOneBar_NoRejectAndNoManufacturedOutlier()
    {
        // Constant raw 100, factor 0.90 everywhere except the middle bar whose adjusted_close is null.
        var dates = new[] { "2026-03-02", "2026-03-03", "2026-03-04", "2026-03-05", "2026-03-06", "2026-03-09", "2026-03-10" };
        var bars = dates.Select((d, i) =>
            i == 3 ? new EodBar(d, null, null, null, 100.0, null, 1000)   // adj absent
                   : new EodBar(d, null, null, null, 100.0, 90.0, 1000)).ToList();

        var report = Gate.Evaluate("XYZ", bars, []);
        Assert.False(report.HasRejects);                                              // absent adj is not a reject
        Assert.DoesNotContain(report.Flags, f => f.Issue == QualityIssue.OutlierReturn); // no basis-mixing spike
    }

    [Fact]
    public void Outlier_LargeNegativeReturn_IsFlagged()
    {
        // A -40% bad-tick among healthy dispersion — the gate is symmetric (|robust z|).
        var rets = (double[])NormalReturns.Clone();
        rets[9] = -0.40;
        var bars = new List<EodBar> { B("2026-06-01", 100.0, 1.0) };
        var close = 100.0;
        for (var i = 0; i < rets.Length; i++) { close *= 1 + rets[i]; bars.Add(B($"2026-06-{i + 2:D2}", close, 1.0)); }

        var report = Gate.Evaluate("SPY", bars, []);
        Assert.Single(report.Flags, f => f.Issue == QualityIssue.OutlierReturn && f.Date == "2026-06-11");
    }

    // The configured Data.OutlierZ actually drives the cutoff: a return whose robust z lands just above
    // it flags, just below does not. (Exact-equality is not asserted — it is float-fragile and, per the
    // strict `>`, practically unreachable.)
    [Fact]
    public void Outlier_RespectsConfiguredCutoff()
    {
        // 10 flat bars (zero returns) then one move of z·0.001: MAD=0 -> scale floored to 0.001, so the
        // final return's robust z == z.
        static QualityReport Run(double z, double cutoff)
        {
            var bars = new List<EodBar>();
            var close = 100.0;
            for (var i = 0; i < 10; i++) bars.Add(B($"2026-11-{i + 1:D2}", close, 1.0));
            close *= 1 + z * 0.001;
            bars.Add(B("2026-11-11", close, 1.0));
            return new DataQualityGate(new DataQualityOptions { OutlierZ = cutoff }).Evaluate("XYZ", bars, []);
        }

        Assert.Contains(Run(8.5, 8.0).Flags, f => f.Issue == QualityIssue.OutlierReturn);        // just above
        Assert.DoesNotContain(Run(7.5, 8.0).Flags, f => f.Issue == QualityIssue.OutlierReturn);  // just below
    }

    // The reconciliation window is (prev.date, cur.date]: an event on the PRIOR session's date does NOT
    // explain a step into the current bar, while an event strictly inside a gap between them DOES.
    [Fact]
    public void Reconciliation_EventOnPriorSessionDate_DoesNotExplain()
    {
        var bars = new List<EodBar> { B("2026-04-01", 50.0, 0.97), B("2026-04-02", 49.9, 0.98) }; // step into 04-02
        var report = Gate.Evaluate("XYZ", bars, [Dividend("2026-04-01")]);                        // event on prev date
        Assert.Single(report.Flags, f => f.Issue == QualityIssue.UnexplainedAdjustment && f.Date == "2026-04-02");
    }

    [Fact]
    public void Reconciliation_EventStrictlyInsideGap_Explains()
    {
        // No bar on 04-02/04-03; the step is 04-01 -> 04-06, and the dividend ex-date 04-03 sits inside.
        var bars = new List<EodBar> { B("2026-04-01", 50.0, 0.97), B("2026-04-06", 49.9, 0.98) };
        var report = Gate.Evaluate("XYZ", bars, [Dividend("2026-04-03")]);
        Assert.DoesNotContain(report.Flags, f => f.Issue == QualityIssue.UnexplainedAdjustment);
    }

    [Fact]
    public void Reconciliation_MatchedDividend_NoFlag()
    {
        var bars = new List<EodBar> { B("2026-04-01", 50.0, 0.97), B("2026-04-02", 49.9, 0.98) }; // factor step
        var report = Gate.Evaluate("XYZ", bars, [Dividend("2026-04-02")]);
        Assert.DoesNotContain(report.Flags, f => f.Issue == QualityIssue.UnexplainedAdjustment);
    }

    [Fact]
    public void Reconciliation_MissingSplit_RaisesAlarm()
    {
        // A 2:1-split-scale factor step (0.5) with no split in the feed is the alarm; supplying the
        // split explains it.
        var bars = new List<EodBar> { B("2026-05-01", 200.0, 0.5), B("2026-05-04", 100.0, 1.0) };

        var missing = Gate.Evaluate("XYZ", bars, []);
        Assert.Single(missing.Flags, f => f.Issue == QualityIssue.UnexplainedAdjustment && f.Date == "2026-05-04");

        var split = new CorporateActionRow
        {
            SecurityId = 1, Type = "split", EffectiveDate = "2026-05-04",
            Ratio = 2.0, ObservedAt = "2026-05-05T00:00:00Z", Source = "eodhd"
        };
        var explained = Gate.Evaluate("XYZ", bars, [split]);
        Assert.DoesNotContain(explained.Flags, f => f.Issue == QualityIssue.UnexplainedAdjustment);
    }

    // A single huge print among otherwise-normal days: the robust (median/MAD) z catches it where a
    // plain sample z would self-mask (the spike inflates the std enough to hide itself under the cutoff).
    // Genuinely varied daily returns (a healthy MAD scale), so the +25% print is the only outlier.
    private static readonly double[] NormalReturns =
        [0.003, -0.005, 0.007, -0.002, 0.004, -0.006, 0.001, -0.003, 0.005, -0.004,
         0.006, -0.001, 0.002, -0.007, 0.003, -0.002, 0.004, -0.005, 0.006];

    [Fact]
    public void Outlier_RobustZ_CatchesSpike_ThatPlainZWouldMask()
    {
        // 20 bars from the varied returns, with one +25% spike swapped in at the day-9→10 step.
        var rets = (double[])NormalReturns.Clone();
        rets[9] = 0.25;
        var bars = new List<EodBar> { B("2026-06-01", 100.0, 1.0) };
        var close = 100.0;
        for (var i = 0; i < rets.Length; i++)
        {
            close *= 1 + rets[i];
            bars.Add(B($"2026-06-{i + 2:D2}", close, 1.0));
        }

        var report = Gate.Evaluate("SPY", bars, []);
        var outliers = report.Flags.Where(f => f.Issue == QualityIssue.OutlierReturn).ToList();
        Assert.Single(outliers);
        Assert.Equal("2026-06-11", outliers[0].Date); // rets[9] is the move into the 11th bar (2026-06-11)

        // Prove a plain sample z of that same return would NOT have crossed 8.0 (self-masking).
        var returns = new double[bars.Count - 1];
        for (var i = 1; i < bars.Count; i++) returns[i - 1] = bars[i].AdjClose!.Value / bars[i - 1].AdjClose!.Value - 1.0;
        var mean = returns.Average();
        var std = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Sum() / (returns.Length - 1));
        Assert.True(Math.Abs(returns[9] - mean) / std < 8.0, "sanity: plain z self-masks below the cutoff");
    }

    [Fact]
    public void Outlier_CleanSeries_NoFlag()
    {
        var bars = new List<EodBar> { B("2026-07-01", 100.0, 1.0) };
        var close = 100.0;
        for (var i = 0; i < NormalReturns.Length; i++)
        {
            close *= 1 + NormalReturns[i];
            bars.Add(B($"2026-07-{i + 2:D2}", close, 1.0));
        }
        var report = Gate.Evaluate("SPY", bars, []);
        Assert.DoesNotContain(report.Flags, f => f.Issue == QualityIssue.OutlierReturn);
    }

    // A mostly-flat (illiquid/halted) series with one bad 40% print: the floored robust scale still
    // catches it — a zero MAD must not mask the spike (nor must the flat days flag).
    [Fact]
    public void Outlier_FlatSeriesWithLoneSpike_StillCaught()
    {
        var bars = new List<EodBar>();
        var close = 50.0;
        for (var i = 0; i < 12; i++)
        {
            if (i == 6) close *= 1.40;      // one 40% bad print; every other day unchanged
            bars.Add(B($"2026-10-{i + 1:D2}", close, 1.0));
        }
        var report = Gate.Evaluate("XYZ", bars, []);
        Assert.Single(report.Flags, f => f.Issue == QualityIssue.OutlierReturn && f.Date == "2026-10-07");
    }

    [Fact]
    public void NanAndNonPositive_AreFailClosedRejects()
    {
        var bars = new List<EodBar>
        {
            B("2026-08-03", 100.0, 1.0),
            new("2026-08-04", null, null, null, double.NaN, double.NaN, 1000), // NaN close
            new("2026-08-05", null, null, null, -5.0, -5.0, 1000),             // non-positive close
        };
        var report = Gate.Evaluate("XYZ", bars, []);

        Assert.Single(report.Flags, f => f.Issue == QualityIssue.NanField && f.Severity == QualitySeverity.Reject);
        Assert.Single(report.Flags, f => f.Issue == QualityIssue.NonPositivePrice && f.Severity == QualitySeverity.Reject);
        Assert.True(report.HasRejects);
        Assert.False(report.IsClean);
    }

    [Fact]
    public void CleanSeries_IsClean()
    {
        string[] dates = ["2026-09-01", "2026-09-02", "2026-09-03", "2026-09-04", "2026-09-08"];
        var bars = new List<EodBar>();
        var close = 100.0;
        foreach (var d in dates) { close *= 1.002; bars.Add(B(d, close, 0.95)); } // constant factor, no steps

        var report = Gate.Evaluate("XYZ", bars, [], dates);
        Assert.True(report.IsClean);
        Assert.False(report.HasRejects);
    }

    [Fact]
    public void Evaluate_NullArguments_ThrowClosed()
    {
        Assert.Throws<ArgumentNullException>(() => Gate.Evaluate("X", null!, []));
        Assert.Throws<ArgumentNullException>(() => Gate.Evaluate("X", [], null!));
        Assert.Throws<ArgumentException>(() => Gate.Evaluate("", [], []));
    }
}
