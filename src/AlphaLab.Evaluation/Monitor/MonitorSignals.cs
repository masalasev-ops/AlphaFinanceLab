namespace AlphaLab.Evaluation.Monitor;

/// <summary>The aggregate overfitting status (OVERFITTING_MONITOR §3), in ascending severity so the
/// aggregate is a simple max.</summary>
public enum MonitorStatus
{
    Healthy = 0,
    Warning = 1,
    Suspect = 2,
    Retired = 3,
}

/// <summary>One signal's row payload: its numeric value, a plain contribution token (persisted to
/// overfitting_checks.contribution), and the status level it contributes to the aggregate.</summary>
public readonly record struct SignalOutcome(string Signal, double? Value, string Contribution, MonitorStatus Status);

/// <summary>
/// The pure Phase-3 monitor signals S2/S3/S6 (OVERFITTING_MONITOR §3, flat pre-calibration anchors from
/// Appendix A). No DB, no clock — deterministic in the inputs. The D63 invariant is baked in: only the
/// anti-predictive tail (S3 &lt; 25th, or a sustained negative rolling alpha) contributes Suspect, so a
/// merely edgeless strategy hovering at its band's median NEVER trips Suspect (beyond the false-alarm rate).
/// </summary>
public static class MonitorSignals
{
    public const double S2ElevatedGapRawSharpe = 0.5;   // Appendix A s2.elevated_gap_raw_sharpe
    public const double S3HealthyAnchor = 95.0;         // Appendix A s3.healthy_percentile_anchor
    public const double S3SuspectAnchor = 25.0;         // Appendix A s3.suspect_below_anchor (anti-predictive tail, D63)
    public const double S6NegativeAlphaT = -1.0;        // Appendix A s6 "negative rolling alpha t < −1"

    /// <summary>S2 — deflated Sharpe: elevated when deflation flips a "positive" Sharpe negative (the gap
    /// is pure selection). Not itself a Suspect signal — S2 is a caution, not a kill.</summary>
    public static SignalOutcome S2(double rawSharpe, double deflatedSharpe)
    {
        var elevated = deflatedSharpe < 0.0 && rawSharpe > S2ElevatedGapRawSharpe;
        return new SignalOutcome("S2", deflatedSharpe, elevated ? "elevated" : "none",
            elevated ? MonitorStatus.Warning : MonitorStatus.Healthy);
    }

    /// <summary>S3 — separation from the matched population (D36). Flat anchors: ≥95th Healthy, &lt;25th
    /// Suspect (the anti-predictive tail — a no-edge strategy at ~50th is "in_band", NOT a status alarm;
    /// its indistinguishability is surfaced by the D63 separation state, not here).</summary>
    public static SignalOutcome S3(double percentile)
    {
        if (percentile < S3SuspectAnchor) return new SignalOutcome("S3", percentile, "suspect", MonitorStatus.Suspect);
        if (percentile >= S3HealthyAnchor) return new SignalOutcome("S3", percentile, "healthy", MonitorStatus.Healthy);
        return new SignalOutcome("S3", percentile, "in_band", MonitorStatus.Healthy);
    }

    /// <summary>
    /// S6 — rolling edge decay, capped at WARNING in Phase 3. A rolling alpha that has sunk inside the
    /// population's central 50% band, or a negative rolling-alpha t-stat, is edge weakening ⇒ Warning.
    ///
    /// Deliberately NOT Suspect on a single evaluation: a single 63-day window has a ~16% chance of a
    /// t &lt; −1 under the null, far above the 5% false-alarm target, so a single-eval Suspect here would
    /// wrongly auto-retire honest no-edge controls — exactly what D63 forbids. The SUSTAINED escalation
    /// (consecutive negative windows → Suspect) is a calibrated Phase-4 refinement; in Phase 3 the
    /// anti-predictive Suspect comes from S3 (&lt; 25th, ~5% by construction), and the auto-retire from a
    /// sustained-Suspect streak.
    /// </summary>
    public static SignalOutcome S6(double rollingAlphaT, bool insideCentralBand)
    {
        if (rollingAlphaT < S6NegativeAlphaT)
            return new SignalOutcome("S6", rollingAlphaT, "elevated_neg_alpha", MonitorStatus.Warning);
        if (insideCentralBand)
            return new SignalOutcome("S6", rollingAlphaT, "elevated_inband", MonitorStatus.Warning);
        return new SignalOutcome("S6", rollingAlphaT, "none", MonitorStatus.Healthy);
    }

    /// <summary>The aggregate status = the max severity over the MONITOR signals ONLY (the whitelist).
    /// Descriptive rows such as signal='turnover_match' are NOT passed here, so they can never move the
    /// verdict (finding 115 / FX-TurnoverMatch-StatusNeutral).</summary>
    public static MonitorStatus Aggregate(IEnumerable<MonitorStatus> monitorSignalStatuses)
    {
        var max = MonitorStatus.Healthy;
        foreach (var s in monitorSignalStatuses) if (s > max) max = s;
        return max;
    }

    public static string ToToken(MonitorStatus s) => s switch
    {
        MonitorStatus.Healthy => "healthy",
        MonitorStatus.Warning => "warning",
        MonitorStatus.Suspect => "suspect",
        MonitorStatus.Retired => "retired",
        _ => "healthy",
    };
}
