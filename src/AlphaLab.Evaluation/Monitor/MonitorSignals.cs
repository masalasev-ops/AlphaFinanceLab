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
/// anti-predictive tail — a SUSTAINED sub-25th S3 or a SUSTAINED negative rolling alpha (Change 3) —
/// contributes Suspect; inside-band decay caps at Warning. So a merely edgeless strategy hovering at its
/// band's median NEVER trips Suspect, and its rare within-null excursions are Warnings, not kills — its
/// indistinguishability is the D63 separation state's job (MASTER §20.8), never a monitor status here.
/// </summary>
public static class MonitorSignals
{
    public const double S2ElevatedGapRawSharpe = 0.5;   // Appendix A s2.elevated_gap_raw_sharpe
    public const double S3HealthyAnchor = 95.0;         // Appendix A s3.healthy_percentile_anchor
    public const double S3SuspectAnchor = 25.0;         // Appendix A s3.suspect_below_anchor (anti-predictive tail, D63)
    public const double S6NegativeAlphaT = -1.0;        // Appendix A s6 "negative rolling alpha t < −1"

    /// <summary>The flat-anchor "sustained" bar (Change 3, D63 conformance): the number of CONSECUTIVE
    /// evaluations — this one included — a strategy must stay below the anti-predictive anchor (flat-S3)
    /// or below the negative-alpha threshold (S6) before the signal contributes SUSPECT. Grounded in the
    /// D56/D63 "sustained" language, NOT in what makes a gate pass: a single (or double) within-null
    /// excursion is a Warning, so a merely edgeless strategy — which crosses the anchor only at the
    /// false-alarm rate — is never flagged Suspect (OVERFITTING_MONITOR §3 "S3 never flags it"), while a
    /// PERSISTENTLY anti-predictive plant crosses it fast. Mirrors <see cref="S3Trajectory"/>'s
    /// sustain_evals for the pre-calibration fallback (curves supply their own once frozen).</summary>
    public const int FlatAnchorSustainEvals = 3;

    /// <summary>S2 — deflated Sharpe: elevated when deflation flips a "positive" Sharpe negative (the gap
    /// is pure selection). Not itself a Suspect signal — S2 is a caution, not a kill.</summary>
    public static SignalOutcome S2(double rawSharpe, double deflatedSharpe)
    {
        var elevated = deflatedSharpe < 0.0 && rawSharpe > S2ElevatedGapRawSharpe;
        return new SignalOutcome("S2", deflatedSharpe, elevated ? "elevated" : "none",
            elevated ? MonitorStatus.Warning : MonitorStatus.Healthy);
    }

    /// <summary>S3 — separation from the matched population (D36). Flat anchors: ≥95th Healthy, &lt;25th the
    /// anti-predictive tail. Change 3 (D63 conformance): a dip below the 25th is Suspect only when SUSTAINED
    /// (<paramref name="sustainEvals"/> consecutive, this one included — a single dip is a Warning), exactly
    /// as <see cref="S3Trajectory"/> requires. A no-edge strategy at ~50th is "in_band" (not a status alarm)
    /// and its rare within-null dips below the 25th are Warnings, never Suspect — "a merely edgeless strategy
    /// … S3 never flags it" (§3); its indistinguishability is the D63 separation state's job, not this. Only a
    /// PERSISTENTLY sub-25th (anti-predictive) plant sustains to Suspect. PRE-CALIBRATION ONLY: once the D56
    /// curves are frozen as config rows, <see cref="S3Trajectory"/> judges instead.</summary>
    public static SignalOutcome S3(double percentile, int priorConsecutiveBelowAnchor, int sustainEvals)
    {
        if (percentile < S3SuspectAnchor)
        {
            return priorConsecutiveBelowAnchor + 1 >= sustainEvals
                ? new SignalOutcome("S3", percentile, "suspect", MonitorStatus.Suspect)
                : new SignalOutcome("S3", percentile, "below_anchor", MonitorStatus.Warning);
        }
        if (percentile >= S3HealthyAnchor) return new SignalOutcome("S3", percentile, "healthy", MonitorStatus.Healthy);
        return new SignalOutcome("S3", percentile, "in_band", MonitorStatus.Healthy);
    }

    /// <summary>
    /// S3 under the CALIBRATED D56 trajectory curves (Phase 4 / checkpoint 4.6): at track length t —
    /// Suspect below P_noise(t) SUSTAINED (sustain_evals consecutive evaluations, this one included;
    /// a single dip is Warning); Healthy above P_edge(t); Warning between (D56's stated bands — the
    /// D63 invariant holds because P_noise is BUILT at the false-alarm quantile of genuinely edgeless
    /// plants, so a no-edge strategy breaches it only at that rate).
    /// </summary>
    public static SignalOutcome S3Trajectory(
        double percentile, int trackDays, double pNoiseAt, double pEdgeAt,
        int priorConsecutiveBelowNoise, int sustainEvals)
    {
        if (percentile < pNoiseAt)
        {
            return priorConsecutiveBelowNoise + 1 >= sustainEvals
                ? new SignalOutcome("S3", percentile, "suspect", MonitorStatus.Suspect)
                : new SignalOutcome("S3", percentile, "below_noise", MonitorStatus.Warning);
        }
        return percentile >= pEdgeAt
            ? new SignalOutcome("S3", percentile, "above_edge", MonitorStatus.Healthy)
            : new SignalOutcome("S3", percentile, "between", MonitorStatus.Warning);
    }

    /// <summary>
    /// S6 — rolling edge decay (OVERFITTING_MONITOR §3, Appendix-A escalation). Change 3 brings the two
    /// arms into D63 conformance:
    ///  • NEGATIVE rolling alpha (t &lt; −1) — the anti-predictive-drift arm — is Warning once (a single
    ///    63-day window has ~16% null probability; a one-eval Suspect would retire honest controls, D63)
    ///    and Suspect only when SUSTAINED (<see cref="FlatAnchorSustainEvals"/> consecutive), so a
    ///    within-null excursion no longer trips it.
    ///  • INSIDE-BAND decay is a CAUTION (Warning) at most and NEVER Suspect. The §3 scope note is explicit:
    ///    "do not tune S6 to catch mid-band lifers" — a strategy that has simply never separated is the
    ///    separation state's job (MASTER §20.8), not S6's. Capping it at Warning means an honest edgeless
    ///    control the population channel keeps can never be RETIRED by S6 (retire is a sustained-Suspect
    ///    streak on the aggregate). S6 still catches genuine anti-predictive drift via the negative-alpha arm.
    /// </summary>
    public static SignalOutcome S6(
        double rollingAlphaT, bool insideCentralBand,
        int priorConsecutiveInsideBand = 0, int priorConsecutiveNegativeT = 0)
    {
        if (rollingAlphaT < S6NegativeAlphaT)
        {
            return priorConsecutiveNegativeT + 1 >= FlatAnchorSustainEvals
                ? new SignalOutcome("S6", rollingAlphaT, "critical_neg_alpha", MonitorStatus.Suspect)
                : new SignalOutcome("S6", rollingAlphaT, "elevated_neg_alpha", MonitorStatus.Warning);
        }
        if (insideCentralBand)
        {
            // Never escalates past Warning (D63 scope note — see the summary): two consecutive inside-band
            // windows are an elevated caution, but inside-band alone is not a kill and cannot retire.
            return priorConsecutiveInsideBand + 1 >= 2
                ? new SignalOutcome("S6", rollingAlphaT, "elevated_inband", MonitorStatus.Warning)
                : new SignalOutcome("S6", rollingAlphaT, "inband", MonitorStatus.Healthy);
        }
        return new SignalOutcome("S6", rollingAlphaT, "none", MonitorStatus.Healthy);
    }

    /// <summary>The S6 contribution tokens that CONTINUE an inside-band streak (inside-band never reaches
    /// a Suspect token now — Change 3, D63).</summary>
    public static bool ContinuesInsideBandStreak(string contribution) =>
        contribution is "inband" or "elevated_inband";

    /// <summary>The S6 contribution tokens that CONTINUE a negative-t streak.</summary>
    public static bool ContinuesNegativeTStreak(string contribution) =>
        contribution is "elevated_neg_alpha" or "critical_neg_alpha";

    /// <summary>The S3 contribution tokens that CONTINUE a below-noise streak (calibrated mode).</summary>
    public static bool ContinuesBelowNoiseStreak(string contribution) =>
        contribution is "below_noise" or "suspect";

    /// <summary>The S3 contribution tokens that CONTINUE a below-anchor streak (flat pre-calibration mode,
    /// Change 3): the sustain that gates the anti-predictive Suspect.</summary>
    public static bool ContinuesBelowAnchorStreak(string contribution) =>
        contribution is "below_anchor" or "suspect";

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
