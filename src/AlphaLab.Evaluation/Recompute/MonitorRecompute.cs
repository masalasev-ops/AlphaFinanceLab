using System.Text.Json;
using AlphaLab.Data;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Monitor;

namespace AlphaLab.Evaluation.Recompute;

/// <summary>One recomputed session for one strategy — enough to reproduce both artefacts the monitor
/// writes: the <c>overfitting_status</c> row and, when a plant would have retired, the go_live_log
/// <c>WouldRevert</c> row.</summary>
public sealed record RecomputedStatus(
    string StrategyId, string AsOf, string Status,
    string S2Contribution, string S3Contribution, string S6Contribution,
    bool WouldRevert);

/// <summary>
/// Re-derives <c>overfitting_status</c> (and the would-be-retire events) from the STORED
/// <c>overfitting_checks</c> rows, under a candidate <see cref="RecomputeSpec"/> — MASTER §25, D106.
///
/// **Streaks are RECOMPUTED, never read.** The live monitor resolves its streaks with
/// <c>TrailingStreak</c>/<c>TrailingSuspectCount</c>, which query the PRIOR STORED rows. Under a changed
/// rule those priors are themselves different, so reading them would silently mix generation 1's history
/// into generation 2's answer — a plausible wrong answer, which §25.4 names as the worst outcome available.
/// This class therefore walks each strategy's sessions in ascending as-of order carrying its own streak
/// state, exactly as a fresh replay would accumulate it.
///
/// **It re-DRIVES the real signal functions rather than copying them.** Every verdict comes from
/// <see cref="MonitorSignals"/>; a second copy of a rule here is a second definition that would drift from
/// the one the arena actually runs.
/// </summary>
public sealed class MonitorRecompute(
    AlphaLabDbContext db, RecomputeSpec spec, string runKind = "replay", BandInputs? bands = null)
{
    /// <summary>A stored check row, reduced to what a recompute can read from it.</summary>
    private sealed record StoredCheck(string Signal, double? Value, string Contribution, string ThresholdJson);

    public IReadOnlyList<RecomputedStatus> Run(IReadOnlyCollection<string> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        // The DerivedBand tier is computed from v1.9.75 — but only when its inputs were actually supplied.
        // A caller that asks for a band-tier spec and forgets to wire BandInputs must NOT silently fall back
        // to token recovery: that recovery is valid only while the negative-alpha threshold is unchanged,
        // which is precisely the case this tier is not needed for (finding 340). Refusing is the same
        // §25.2 conformance it always was, now scoped to the case that genuinely cannot be answered.
        if (spec.Tier == RecomputeTier.DerivedBand && bands is null)
        {
            throw new RecomputeRefusedException(
                "Recompute refused: this specification needs DerivedBand inputs (member window alphas " +
                "re-derived from control_equity, and the subject's own window alpha from equity_curve), and " +
                "none were supplied. Recovering band membership from the stored contribution token is valid " +
                "ONLY while the negative-alpha threshold is unchanged — moving it is exactly what makes the " +
                "recovery invalid (finding 340), so answering from stored columns here would look correct " +
                "and be wrong.");
        }

        var autoRetireEvals = spec.Int(RecomputeParameters.AutoRetireEvals, OverfittingMonitor.AutoRetireConsecutiveSuspect);

        // ---- P25 TRIPWIRE (D141 sweep) -----------------------------------------------------------------
        // The live monitor resolves S6 patience from the CALIBRATED CONFIG ROW as-of the run's watermark
        // (OverfittingMonitor.ResolveAutoRetirePatience); this class reads a COMPILE-TIME CONSTANT. While
        // both are 4 — the chain only ever freezes the constant, so divergence takes an OPERATOR raise —
        // the two agree and every artefact reproduces. The moment they part, `WouldRevert` would be
        // reproduced under a patience the generation was never produced with: D140's rule, on one of the
        // three parity artefacts.
        //
        // Fixing that is P25 and needs its own D-number (it changes what parity MEANS, rule 25), so this is
        // NOT the fix — it is the trigger, and it exists because a recorded proposal with no trigger is a
        // note that arms silently. It refuses rather than warns, on the D139 pattern: the failure mode being
        // prevented is a confident wrong answer with a filename.
        //
        // Deliberately watermark-free: ANY stored version differing from the constant trips it. The
        // generation's own watermark is not in scope here, and a conservative check that fires too often is
        // the right error to make when the alternative is a plausible wrong artefact.
        if (!spec.Overrides.ContainsKey(RecomputeParameters.AutoRetireEvals))
        {
            var divergent = db.Config
                .Where(c => c.Key == CalibratedKeys.S6AutoRetireEvals)
                .Select(c => new { c.Version, c.ValueJson })
                .AsEnumerable()
                .Where(c => int.TryParse(c.ValueJson, out var v) && v >= 2 && v != autoRetireEvals)
                .OrderBy(c => c.Version)
                .ToList();
            if (divergent.Count > 0)
            {
                throw new RecomputeRefusedException(
                    $"Recompute refused (P25): the store holds {CalibratedKeys.S6AutoRetireEvals} version(s) " +
                    $"[{string.Join(", ", divergent.Select(d => $"v{d.Version}={d.ValueJson}"))}] that differ from " +
                    $"the constant this harness reproduces with ({autoRetireEvals}). The live monitor resolves " +
                    "that row; this class does not, so WouldRevert would be reproduced under an auto-retire " +
                    "patience the generation was never produced with (D140's rule, D141's sweep). Resolving the " +
                    "patience from the stored generation is P25 and takes its own decision number. Until then, " +
                    "pass --set " + RecomputeParameters.AutoRetireEvals + "=<the generation's value> to state " +
                    "the patience explicitly, which makes the choice visible in the run's own description.");
            }
        }

        var results = new List<RecomputedStatus>();

        foreach (var strategyId in subjects.OrderBy(s => s, StringComparer.Ordinal))
        {
            // One strategy's whole history, ascending — the order the streaks accumulate in.
            var bySession = db.OverfittingChecks
                .Where(c => c.StrategyId == strategyId && c.RunKind == runKind)
                .Select(c => new { c.AsOf, c.Signal, c.Value, c.Contribution, c.ThresholdJson })
                .AsEnumerable()
                .GroupBy(c => c.AsOf, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            // The streaks the monitor carries, plus the aggregate's consecutive-Suspect count.
            var belowAnchor = 0;    // S3 flat-anchor / trajectory below-noise streak
            var aboveEdge = 0;      // S3 trajectory above-edge streak (D148 — Healthy is sustained too)
            var insideBand = 0;     // S6 inside-band streak
            var negativeT = 0;      // S6 negative-alpha streak
            var suspectRun = 0;     // consecutive 'suspect' statuses

            // Plants are retire-EXEMPT under replay (D100 / Change 1): the would-be retire is recorded but
            // the status stays 'suspect' and the plant keeps emitting rows. Reproducing that exemption is
            // what makes the plant cohorts recomputable in BOTH directions (finding 338).
            var exemptFromRetire = runKind != "live" && PlantCohorts.IsPlantId(strategyId);

            foreach (var session in bySession)
            {
                var checks = session.ToDictionary(
                    c => c.Signal,
                    c => new StoredCheck(c.Signal, c.Value, c.Contribution, c.ThresholdJson),
                    StringComparer.Ordinal);

                var s2 = RecomputeS2(checks);
                var s3 = RecomputeS3(checks, belowAnchor, aboveEdge);
                var s6 = RecomputeS6(checks, strategyId, session.Key, insideBand, negativeT);

                var aggregate = MonitorSignals.Aggregate([s2.Status, s3.Status, s6.Status]);
                var wouldRetire = aggregate == MonitorStatus.Suspect && suspectRun >= autoRetireEvals - 1;
                var wouldRevert = wouldRetire && exemptFromRetire;
                if (wouldRetire && !exemptFromRetire) aggregate = MonitorStatus.Retired;

                var token = MonitorSignals.ToToken(aggregate);
                results.Add(new RecomputedStatus(
                    strategyId, session.Key, token, s2.Contribution, s3.Contribution, s6.Contribution, wouldRevert));

                // Advance the streaks from THIS evaluation's recomputed contributions.
                belowAnchor = MonitorSignals.ContinuesBelowAnchorStreak(s3.Contribution)
                              || MonitorSignals.ContinuesBelowNoiseStreak(s3.Contribution) ? belowAnchor + 1 : 0;
                aboveEdge = MonitorSignals.ContinuesAboveEdgeStreak(s3.Contribution) ? aboveEdge + 1 : 0;
                insideBand = MonitorSignals.ContinuesInsideBandStreak(s6.Contribution) ? insideBand + 1 : 0;
                negativeT = MonitorSignals.ContinuesNegativeTStreak(s6.Contribution) ? negativeT + 1 : 0;
                suspectRun = token == "suspect" ? suspectRun + 1 : 0;

                // A non-exempt retire ends the strategy's history: it leaves the promotable set and stops
                // being evaluated (EffectiveStatus). Stopping here mirrors that; the harness only reaches
                // this line for subjects the retire-exempt guard admitted (finding 338).
                if (aggregate == MonitorStatus.Retired) break;
            }
        }

        return results;
    }

    // ---- the three signals, re-driven over stored inputs ------------------------------------------------

    /// <summary>S2 — the raw Sharpe is not the stored <c>value</c> (that is the DEFLATED one), but the
    /// monitor records it in <c>threshold_json</c>, so the rule stays pure over persisted inputs.</summary>
    private SignalOutcome RecomputeS2(IReadOnlyDictionary<string, StoredCheck> checks)
    {
        if (!checks.TryGetValue("S2", out var c) || c.Value is not { } deflated) return Absent("S2");

        var rawSharpe = ReadDouble(c.ThresholdJson, "raw_sharpe");
        if (rawSharpe is not { } raw) return new SignalOutcome("S2", deflated, c.Contribution, StatusOf(c.Contribution, "S2"));

        var elevatedGap = spec.Double(RecomputeParameters.S2ElevatedGapRawSharpe, MonitorSignals.S2ElevatedGapRawSharpe);
        var elevated = deflated < 0.0 && raw > elevatedGap;
        return new SignalOutcome("S2", deflated, elevated ? "elevated" : "none",
            elevated ? MonitorStatus.Warning : MonitorStatus.Healthy);
    }

    /// <summary>S3 — the stored value IS the percentile, so both the flat-anchor and the calibrated
    /// trajectory forms re-threshold purely over stored inputs. Which form ran is recoverable from
    /// <c>threshold_json</c>: the trajectory records `p_noise_at`/`p_edge_at`, the flat form records the
    /// anchors. An `undefined` row (no matched population) had no percentile and stays as it was.</summary>
    private SignalOutcome RecomputeS3(IReadOnlyDictionary<string, StoredCheck> checks, int priorBelow, int priorAbove)
    {
        if (!checks.TryGetValue("S3", out var c)) return Absent("S3");
        if (c.Value is not { } percentile) return new SignalOutcome("S3", null, c.Contribution, StatusOf(c.Contribution, "S3"));

        if (ReadDouble(c.ThresholdJson, "p_noise_at") is { } pNoise && ReadDouble(c.ThresholdJson, "p_edge_at") is { } pEdge)
        {
            var sustain = spec.Int(RecomputeParameters.S3SustainEvals,
                (int)(ReadDouble(c.ThresholdJson, "sustain_evals") ?? MonitorSignals.FlatAnchorSustainEvals));
            var trackDays = (int)(ReadDouble(c.ThresholdJson, "track_days") ?? 0);
            return MonitorSignals.S3Trajectory(percentile, trackDays, pNoise, pEdge, priorBelow, sustain, priorAbove);
        }

        // Flat pre-calibration anchors. The anchors live on MonitorSignals as constants, so an override is
        // applied by re-deriving the comparison here rather than by mutating the shared rule.
        var suspectAnchor = spec.Double(RecomputeParameters.S3SuspectAnchor, MonitorSignals.S3SuspectAnchor);
        var healthyAnchor = spec.Double(RecomputeParameters.S3HealthyAnchor, MonitorSignals.S3HealthyAnchor);
        var sustainEvals = spec.Int(RecomputeParameters.S3SustainEvals,
            (int)(ReadDouble(c.ThresholdJson, "sustain_evals") ?? MonitorSignals.FlatAnchorSustainEvals));

        if (percentile < suspectAnchor)
        {
            return priorBelow + 1 >= sustainEvals
                ? new SignalOutcome("S3", percentile, "suspect", MonitorStatus.Suspect)
                : new SignalOutcome("S3", percentile, "below_anchor", MonitorStatus.Warning);
        }
        return percentile >= healthyAnchor
            ? new SignalOutcome("S3", percentile, "healthy", MonitorStatus.Healthy)
            : new SignalOutcome("S3", percentile, "in_band", MonitorStatus.Healthy);
    }

    /// <summary>
    /// S6 — the stored value IS the rolling alpha t-stat. Band membership is recovered from the
    /// contribution token, which is sound ONLY while the negative-alpha threshold is unchanged: a row that
    /// took the negative branch never evaluated the band, so it records no band information (finding 340).
    /// Moving that threshold is classified <see cref="RecomputeTier.DerivedBand"/> and refused upstream, so
    /// by the time control reaches here the recovery is valid.
    /// </summary>
    private SignalOutcome RecomputeS6(
        IReadOnlyDictionary<string, StoredCheck> checks, string strategyId, string asOf,
        int priorInside, int priorNegative)
    {
        if (!checks.TryGetValue("S6", out var c)) return Absent("S6");
        if (c.Value is not { } t) return new SignalOutcome("S6", null, c.Contribution, StatusOf(c.Contribution, "S6"));

        var sustain = spec.Int(RecomputeParameters.S6SustainEvals, MonitorSignals.FlatAnchorSustainEvals);
        var negativeT = spec.Double(RecomputeParameters.S6NegativeAlphaT, MonitorSignals.S6NegativeAlphaT);

        // BAND POSITION: derived when the tier supplies the inputs, recovered from the contribution
        // otherwise. 6.5 raised what this has to answer — the rule now needs POSITION (below / inside /
        // above), not merely membership, because the anti arm fires only BELOW the band. So the
        // recovery path can no longer serve an S6 rule change at all: a stored `elevated_neg_alpha` row
        // was written by a rule that returned before the band was consulted, and therefore records
        // nothing about which side of it the strategy was on. That is finding 340 one level deeper, and
        // it is refused out loud rather than guessed.
        MonitorSignals.BandPosition band;
        if (bands is not null)
        {
            var lowPct = spec.Double(RecomputeParameters.S6BandLowPct, 25.0);
            var highPct = spec.Double(RecomputeParameters.S6BandHighPct, 75.0);
            if (bands.StrategyWindow(strategyId, asOf) is { } w && bands.MemberBand(asOf, lowPct, highPct) is { } b)
            {
                band = w.Alpha < b.Lo ? MonitorSignals.BandPosition.Below
                     : w.Alpha > b.Hi ? MonitorSignals.BandPosition.Above
                     : MonitorSignals.BandPosition.Inside;
            }
            else
            {
                // No window or no member band at this session — the monitor itself emits
                // `insufficient_track` there, so the stored row is reproduced rather than re-derived.
                return new SignalOutcome("S6", t, c.Contribution, StatusOf(c.Contribution, "S6"));
            }
        }
        else if (MonitorSignals.ContinuesNegativeTStreak(c.Contribution))
        {
            // Unrecoverable by construction (see above). A spec that did not touch S6 is entitled to the
            // stored row — its inputs are unchanged — and a spec that DID touch S6 is a DerivedBand tier,
            // which cannot reach this branch: RecomputeMonitor refuses a band-tier spec with no
            // BandInputs before any row is walked.
            return new SignalOutcome("S6", t, c.Contribution, StatusOf(c.Contribution, "S6"));
        }
        else
        {
            band = MonitorSignals.ContinuesInsideBandStreak(c.Contribution)
                ? MonitorSignals.BandPosition.Inside
                : MonitorSignals.BandPosition.Above;   // "none": t was >= the threshold, so the arm
                                                       // cannot fire and Above vs Below is immaterial.
        }

        // ONE DEFINITION. The rule is MonitorSignals.S6 itself, driven with the spec's overrides — not a
        // second copy of its branch order kept in step by hand. This class's own instruction is that a
        // second copy is a second definition that would drift, and the pre-6.5 code was exactly that:
        // the branch order was duplicated here, so the remedy would have had to be applied twice and
        // FX-RecomputeParity would have gone green either way.
        return MonitorSignals.S6(t, band, priorInside, priorNegative, negativeT, sustain);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>A signal with no stored row for this session contributes Healthy, matching the monitor's
    /// own `insufficient_track` / `undefined` outcomes.</summary>
    private static SignalOutcome Absent(string signal) =>
        new(signal, null, "insufficient_track", MonitorStatus.Healthy);

    /// <summary>The status a stored contribution token carried, for rows the recompute passes through
    /// unchanged (no value ⇒ no rule to re-apply).</summary>
    private static MonitorStatus StatusOf(string contribution, string signal) => (signal, contribution) switch
    {
        (_, "suspect") or (_, "critical_neg_alpha") => MonitorStatus.Suspect,
        (_, "elevated") or (_, "below_anchor") or (_, "below_noise") or (_, "between")
            or (_, "elevated_inband") or (_, "elevated_neg_alpha") => MonitorStatus.Warning,
        _ => MonitorStatus.Healthy,
    };

    private static double? ReadDouble(string json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : null;
    }
}
