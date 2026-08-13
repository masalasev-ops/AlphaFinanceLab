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
        int priorConsecutiveBelowNoise, int sustainEvals, int priorConsecutiveAboveEdge)
    {
        if (percentile < pNoiseAt)
        {
            return priorConsecutiveBelowNoise + 1 >= sustainEvals
                ? new SignalOutcome("S3", percentile, "suspect", MonitorStatus.Suspect)
                : new SignalOutcome("S3", percentile, "below_noise", MonitorStatus.Warning);
        }

        if (percentile < pEdgeAt) return new SignalOutcome("S3", percentile, "between", MonitorStatus.Warning);

        // HEALTHY REQUIRES SUSTAIN TOO (D148). OVERFITTING_MONITOR §3 states the band as
        // "Suspect below P_noise(t) SUSTAINED · Healthy above P_edge(t) SUSTAINED · Warning between",
        // and only the Suspect arm had it. The asymmetry ran the flattering way: one lucky print above the
        // edge curve read Healthy, while one unlucky print below the noise curve correctly read only
        // Warning. A signal whose adverse arm demands persistence and whose favourable arm does not is
        // biased by construction, and this is the signal the D63 separation state mirrors.
        return priorConsecutiveAboveEdge + 1 >= sustainEvals
            ? new SignalOutcome("S3", percentile, "above_edge", MonitorStatus.Healthy)
            : new SignalOutcome("S3", percentile, "approaching_edge", MonitorStatus.Warning);
    }

    /// <summary>
    /// Where a strategy's rolling window alpha sits relative to its population's central band (6.5).
    ///
    /// Replaces the bool <c>insideCentralBand</c>, which could not express the distinction the remedy
    /// turns on: BELOW a cost-matched band and ABOVE one are opposite findings, and collapsing them to
    /// "not inside" is what let the anti-predictive arm fire on a strategy that was beating its null.
    /// D63 fixes the vocabulary — "an edgeless strategy is never *below* a cost-matched band" — so
    /// below IS the anti-predictive signature and the type now says so.
    /// </summary>
    public enum BandPosition
    {
        /// <summary>Under the band's lower percentile — the anti-predictive signature (D63).</summary>
        Below,

        /// <summary>Within the central 50 % — where an edgeless strategy sits by construction.</summary>
        Inside,

        /// <summary>Over the band's upper percentile — outperforming its own null.</summary>
        Above,
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
    /// <param name="negativeAlphaT">The anti-predictive threshold. Overridable so the D106/D117 harness
    /// can SCORE a move to it through this very method rather than through a second copy of the rule —
    /// MonitorRecompute's own instruction ("a second copy of a rule here is a second definition that
    /// would drift"). Defaults to the Appendix-A value.</param>
    /// <param name="sustainEvals">The consecutive-evaluation bar for the anti arm, overridable for the
    /// same reason.</param>
    public static SignalOutcome S6(
        double rollingAlphaT, BandPosition band,
        int priorConsecutiveInsideBand = 0, int priorConsecutiveNegativeT = 0,
        double negativeAlphaT = S6NegativeAlphaT, int sustainEvals = FlatAnchorSustainEvals)
    {
        // THE BAND IS CONSULTED FIRST, AND THAT ORDERING IS THE REMEDY (6.5, finding 280).
        //
        // Until now the negative-alpha arm returned BEFORE the band was ever looked at, so a strategy
        // sitting at the median of its own cost-matched null could still be called anti-predictive on a
        // t-stat measured against ZERO. For the D64 no-edge plants that is not an edge case, it is the
        // normal state: the plant is a 40-name daily-churn COST-PAYING population member measured against
        // a near-cost-free buy-and-hold benchmark, so its window alpha carries the family's ~21.9 %/yr
        // drag and t < −1 lands on the large majority of evaluations. The D63-conformant verdict was
        // computed on the very same call and thrown away by the early return.
        //
        // Both source documents already required the band to be part of the judgement, so this is
        // CONFORMANCE rather than a new rule:
        //   §3  — "Rolling-window (63d) net alpha trend vs the strategy's own history **and vs its
        //          population band**".
        //   D63 — "an edgeless strategy sits at the MEDIAN of its band … an edgeless strategy is never
        //          *below* a cost-matched band."
        // BELOW THE BAND is therefore what "anti-predictive" means here; below ZERO is what "pays costs"
        // means, and the two were being conflated.
        //
        // Why no threshold could have fixed it — the argument that makes this behavioural, and it cites
        // no metric moving: the anti plant's PLANTED signal is −2 %/yr against a common-mode drag of
        // −21.9 %/yr, a ratio of about 1:11, giving ~0.19 of expected t-separation against a noise SD of
        // 1.0. No cut point on a statistic whose signal is a tenth of its shared offset can separate
        // anti-predictive from no-edge. Moving the threshold only moves WHICH evaluations trip, never
        // WHICH COHORTS do.
        if (band == BandPosition.Inside)
        {
            // Never escalates past Warning (D63 scope note — see the summary): two consecutive inside-band
            // windows are an elevated caution, but inside-band alone is not a kill and cannot retire.
            return priorConsecutiveInsideBand + 1 >= 2
                ? new SignalOutcome("S6", rollingAlphaT, "elevated_inband", MonitorStatus.Warning)
                : new SignalOutcome("S6", rollingAlphaT, "inband", MonitorStatus.Healthy);
        }

        // The anti-predictive arm, now with the precondition §3 always specified: BELOW the band AND a
        // sustained negative t. Above the band with a negative t is a cost-paying outperformer, not drift.
        if (band == BandPosition.Below && rollingAlphaT < negativeAlphaT)
        {
            return priorConsecutiveNegativeT + 1 >= sustainEvals
                ? new SignalOutcome("S6", rollingAlphaT, "critical_neg_alpha", MonitorStatus.Suspect)
                : new SignalOutcome("S6", rollingAlphaT, "elevated_neg_alpha", MonitorStatus.Warning);
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

    /// <summary>The S3 contribution tokens that CONTINUE an above-edge streak (calibrated mode, D148).
    /// `approaching_edge` is above the edge curve but not yet sustained, so it CONTINUES the streak it is
    /// building; `above_edge` is the sustained state itself. `between` and the below-noise tokens break it —
    /// a path that drops back under the edge curve starts its count again.</summary>
    public static bool ContinuesAboveEdgeStreak(string contribution) =>
        contribution is "approaching_edge" or "above_edge";

    /// <summary>The S3 contribution tokens that CONTINUE a below-anchor streak (flat pre-calibration mode,
    /// Change 3): the sustain that gates the anti-predictive Suspect.</summary>
    public static bool ContinuesBelowAnchorStreak(string contribution) =>
        contribution is "below_anchor" or "suspect";

    /// <summary>
    /// OVERFITTING_MONITOR §3's escalation count: <b>Suspect = ≥1 signal critical, OR ≥3 elevated</b>.
    /// The literal number from the specification, not a derived one.
    /// </summary>
    public const int ElevatedEscalationCount = 3;

    /// <summary>
    /// The aggregate status over the MONITOR signals ONLY (the whitelist). Descriptive rows such as
    /// signal='turnover_match' are NOT passed here, so they can never move the verdict (finding 115 /
    /// FX-TurnoverMatch-StatusNeutral).
    ///
    /// <para><b>§3 IS TWO RULES AND THIS USED TO IMPLEMENT ONE (D158, finding 433).</b> "Suspect = ≥1
    /// signal critical, or ≥3 elevated" — the first arm is a max, the second is a COUNT, and the function
    /// was a bare max. Three elevated signals aggregated to Warning, never Suspect, so the escalation
    /// existed only in the specification.</para>
    ///
    /// <para><b>IT IS INERT AT TODAY'S SIGNAL COUNT, AND THAT IS MEASURED RATHER THAN ARGUED.</b> Only
    /// S2/S3/S6 are implemented, so "≥3 elevated" demands UNANIMITY among all three. Across the frozen
    /// generation's 95,769 stored status rows the concurrent-elevated count never exceeded TWO (54,573
    /// rows at 0, 37,905 at 1, 3,291 at 2, zero at 3), so switching this on changes nothing that has ever
    /// been stored. `D158_ThreeElevatedSignalsEscalateToSuspect` is therefore necessarily a SYNTHETIC
    /// fixture — without it the rule would be unfalsifiable, which is what it was before.</para>
    ///
    /// <para><b>THE WIDENING IS THE REAL SUBJECT (P27).</b> §3 specifies S1–S8; three are built. "≥3" is a
    /// unanimity bar at three signals and a MINORITY bar at eight, so this rule tightens sharply — and
    /// silently — as each new signal lands, with no test able to notice the transition because none can
    /// fire today. The auto-retire patience (`AutoRetireConsecutiveSuspect`, 4 consecutive) sits directly
    /// on this aggregate and was calibrated when Suspect-by-count was unreachable. Both must be re-derived
    /// at the moment the whitelist grows; that is a tripwire recorded in PROGRESS, not a knob built now.</para>
    /// </summary>
    public static MonitorStatus Aggregate(IEnumerable<MonitorStatus> monitorSignalStatuses)
    {
        var max = MonitorStatus.Healthy;
        var elevated = 0;
        foreach (var s in monitorSignalStatuses)
        {
            if (s > max) max = s;
            if (s == MonitorStatus.Warning) elevated++;
        }

        // The critical arm wins outright and is checked FIRST, so Retired keeps its own severity rather
        // than being flattened to Suspect by the count arm.
        if (max >= MonitorStatus.Suspect) return max;

        return elevated >= ElevatedEscalationCount ? MonitorStatus.Suspect : max;
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
