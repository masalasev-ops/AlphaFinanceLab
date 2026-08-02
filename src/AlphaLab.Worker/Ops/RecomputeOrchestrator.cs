using System.Globalization;
using System.Text;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Evaluation.Recompute;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>
/// The `replay-recompute` chain (D106/D117; MASTER §25): resolve the spec → run the harness over the
/// stored generation → write the archived markdown report. **There is no step (7).** `replay-calibrate`
/// ends in a config freeze; this deliberately does not, because D117 clause 1 settles §25.5(a) as
/// report-only: nothing here writes a row, so a recomputed answer can never be mistaken for a recorded one.
///
/// The report lands in the repo's TRACKED docs/calibration for the same reason the calibration report does
/// (finding 276): `dotnet run --project src/AlphaLab.Worker` has cwd = the project dir, so a bare relative
/// path would bury the artefact under src/. The anchoring helpers are reused from
/// <see cref="CalibrationOrchestrator"/> rather than re-derived.
/// </summary>
public sealed class RecomputeOrchestrator(
    AlphaLabDbContext db,
    GateOptions gate,
    ArenaOptions arena,
    ILogger<RecomputeOrchestrator> logger)
{
    public const string DefaultReportDir = "docs/calibration";

    public sealed record RecomputeRun(RecomputeReportModel Report, string ReportPath);

    /// <summary>Runs the harness and archives its report. <paramref name="today"/> is injected rather than
    /// read from the clock so the fixture can name the artefact deterministically.</summary>
    public RecomputeRun Run(
        RecomputeSpec spec, string today, string? reportBaseDir = null, string runKind = "replay")
    {
        ArgumentNullException.ThrowIfNull(spec);

        logger.LogInformation(
            "replay-recompute: arena {Arena}, spec {Spec} (tier {Tier}) — report-only, no rows are written.",
            arena.Id, spec.Describe(), spec.Tier);

        var report = new RecomputeHarness(db, gate, runKind).Run(spec);

        var baseDir = CalibrationOrchestrator.ResolveReportBaseDir(
            reportBaseDir ?? DefaultReportDir, CalibrationOrchestrator.FindRepoRoot());
        var dir = Path.Combine(baseDir, arena.Id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{today}-recompute-{Sanitize(spec.Name)}.md");
        File.WriteAllText(path, Render(report, spec, today), new UTF8Encoding(false));

        if (report.ParityHolds)
        {
            logger.LogInformation("replay-recompute: all three artefacts agree exactly. Report: {Path}", path);
        }
        else
        {
            // Not an error — a DIFFERENCE is the harness's product when a spec changes a rule. It is only a
            // failure when the spec was the parity spec, which the report states in its own words.
            logger.LogWarning(
                "replay-recompute: {Statuses} status, {Promotions} promotion, {WouldReverts} would-revert difference(s). Report: {Path}",
                report.Statuses.Differing, report.Promotions.Differing, report.WouldReverts.Differing, path);
        }

        return new RecomputeRun(report, path);
    }

    /// <summary>
    /// The finding-280 section. The defect is NOT that the monitor flags too much — it is that it flags the
    /// two cohorts at IDENTICAL rates, so only the differential can judge a fix. A rule change that
    /// suppresses both equally has fixed nothing while the raw counts look like progress.
    /// </summary>
    private static void AppendSeparation(StringBuilder sb, CohortSeparationResult sep)
    {
        sb.AppendLine("## Cohort separation — the finding-280 measurement");
        sb.AppendLine();
        sb.AppendLine("*D63 is asymmetric: `anti` SHOULD be caught, `noedge` should NOT — \"S3 never flags a merely edgeless strategy\" (OVERFITTING_MONITOR §3). Finding 280 measured both at 50/50 **live at session 639** (~2.5y), which is why this is reported at several horizons: the ever-Suspect predicate SATURATES, and over a full 20-year window every cohort reaches it. A single full-window number would discriminate nothing — finding 289's window-monotonicity lesson, applied to a different EVER predicate.*");
        sb.AppendLine();
        foreach (var h in sep.Horizons)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {h.Label}");
            sb.AppendLine();
            sb.AppendLine("| cohort | n | ever-Suspect stored | ever-Suspect recomputed |");
            sb.AppendLine("|---|---:|---|---|");
            foreach (var c in h.Cohorts)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{c.Kind}` | {c.Cohort} | {c.StoredEverSuspect}/{c.Cohort} | **{c.RecomputedEverSuspect}/{c.Cohort}** |");
            }
            sb.AppendLine();
            if (h.StoredSeparation is { } was && h.RecomputedSeparation is { } now)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Separation (anti − noedge): **{was:0.00} → {now:0.00}**{(h.Saturated ? "  — *SATURATED: both judged cohorts are within one plant of the ceiling, so this horizon cannot discriminate and the sign of its separation is noise*" : "")}");
                sb.AppendLine();
            }
        }

        // The non-saturating instrument (finding 346). D63's asymmetry is not that anti plants are EVER
        // caught and edgeless ones never are — over a long enough window both are. It is that anti should be
        // caught FAST and edgeless slowly, and speed is distinguishable however long the window runs.
        if (sep.Speeds.Count > 0)
        {
            sb.AppendLine("### Detection SPEED — median sessions to first Suspect");
            sb.AppendLine();
            sb.AppendLine("*The ever-Suspect rates above saturate; this does not. `anti_detection_speed` is named for speed but is itself an EVER predicate (\"<50 % of anti plants ever Suspect\"), so this is the first thing in the corpus that measures what that name says.*");
            sb.AppendLine();
            sb.AppendLine("| cohort | n | median sessions stored → recomputed | never flagged stored → recomputed |");
            sb.AppendLine("|---|---:|---|---|");
            foreach (var sp in sep.Speeds)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{sp.Kind}` | {sp.Cohort} | {sp.StoredMedianSessions?.ToString(CultureInfo.InvariantCulture) ?? "—"} → **{sp.RecomputedMedianSessions?.ToString(CultureInfo.InvariantCulture) ?? "—"}** | {sp.StoredNeverFlagged} → **{sp.RecomputedNeverFlagged}** |");
            }
            sb.AppendLine();
            var (gapWas, gapNow) = sep.SpeedGap;
            if (gapWas is { } gw && gapNow is { } gn)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"**Speed gap (anti median − noedge median): {gw} → {gn} sessions.** NEGATIVE is the D63 direction — anti caught sooner than merely edgeless.");
                sb.AppendLine();
                sb.AppendLine(gn == gw
                    ? "Unchanged: this rule change does not alter how much sooner anti-predictive plants are caught than edgeless ones."
                    : gn < gw
                        ? "**Improved** — the gap widened in the D63 direction: the change delays flagging of edgeless plants more than it delays anti-predictive ones."
                        : "**WORSE** — the gap narrowed or reversed: the change delays anti-predictive detection at least as much as edgeless detection, which is the monitor going quiet on the cohort it exists to catch.");
                sb.AppendLine();
            }
        }

        var d = sep.Discriminating;
        sb.AppendLine("**Verdict (read from the shortest non-saturated horizon):**");
        sb.AppendLine();
        if (d is null || d.StoredSeparation is not { } dWas || d.RecomputedSeparation is not { } dNow)
        {
            sb.AppendLine("**Not readable — every horizon is saturated.** The instrument cannot judge this change, and that is a statement about the MEASUREMENT, not evidence that the change did nothing. A shorter horizon or a per-evaluation flag rate is needed before any finding-280 candidate can be scored.");
        }
        else
        {
            // The change is reported in PLANTS, not just as a rate: a rate difference at or below 1/n is one
            // plant moving, and calling that an improvement is reading the noise floor as data (finding 344).
            var delta = dNow - dWas;
            var resolution = d.Resolution ?? 0.0;
            var plants = resolution > 0 ? Math.Abs(delta) / resolution : 0.0;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"At **{d.Label}**: separation {dWas:0.00} → {dNow:0.00} (a move of ~{plants:0.#} plant(s); this instrument cannot resolve less than {resolution:0.00}).");
            sb.AppendLine();
            sb.AppendLine(Math.Abs(delta) <= resolution + 1e-9
                ? "**Within the instrument's resolution — NOT a result.** The separation moved by at most one plant, which is the smallest difference this cohort size can express. Read it as *no measured effect on finding 280*, never as a direction."
                : delta > 0
                    ? "**Improved.** The change separates anti-predictive plants from merely edgeless ones by more than the instrument's resolution, which is the direction D63 conformance requires. Judge it on this number, never on the raw status count."
                    : "**WORSE — this change moves finding 280 backwards.** It suppresses the cohort that SHOULD be caught more than the one that should not. A falling status count is not progress here; it is the monitor going quiet on anti-predictive drift.");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// The C-1 detection-power section — the part that turns "promotions changed" into an answer about the
    /// GATE. `α*(H)` is the empirical detectability floor: the smallest simulated edge whose detection
    /// curve reaches `Gate.Power` within the horizon. `unreachable` is finding 336's state — no rung gets
    /// there, so the floor is `+∞` and NO candidate is admissible until the curves are re-measured.
    /// </summary>
    private static void AppendDetectionPower(StringBuilder sb, DetectionPowerComparison dp)
    {
        static string Floor(double? a) => a switch
        {
            null => "n/a (no rungs)",
            { } v when double.IsPositiveInfinity(v) => "**unreachable (+∞)** — no rung reaches the power at this horizon",
            { } v => v.ToString("P2", CultureInfo.InvariantCulture) + "/yr",
        };

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"## C-1 detection power — recomputed vs frozen (horizon {dp.HorizonYears}y = {dp.HorizonSessions} sessions, power {dp.Power:P0})");
        sb.AppendLine();
        sb.AppendLine("*The monthly edge ladder IS the C-1 sweep (Change 4 / D101 — daily cannot promote under its cost drag). Same denominator, same session-index grid and same selection rule as the frozen curve, or the two would not be comparable.*");
        sb.AppendLine();
        sb.AppendLine("| rung | seeds | promoted (stored → recomputed) | P(promoted by H) stored → recomputed | median sessions stored → recomputed |");
        sb.AppendLine("|---:|---:|---|---|---|");
        foreach (var g in dp.Rungs)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {g.AlphaAnnPct:0.##} %/yr | {g.Seeds} | {g.StoredPromoted} → **{g.RecomputedPromoted}** | {g.StoredPAtHorizon:0.00} → **{g.RecomputedPAtHorizon:0.00}** | {g.StoredMedianSessions?.ToString(CultureInfo.InvariantCulture) ?? "—"} → **{g.RecomputedMedianSessions?.ToString(CultureInfo.InvariantCulture) ?? "—"}** |");
        }
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **α\\*(H) implied by the FROZEN promotions:** {Floor(dp.StoredAlphaStarAnn)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **α\\*(H) implied by the RECOMPUTED promotions:** {Floor(dp.RecomputedAlphaStarAnn)}");
        sb.AppendLine();

        sb.AppendLine(GateVerdict(dp.StoredAlphaStarAnn, dp.RecomputedAlphaStarAnn));
        sb.AppendLine();
    }

    /// <summary>
    /// The sentence a human actually reads. Public and pure so the four-way branch is pinned by fixture
    /// rather than buried in string building: getting "the gate would reopen" backwards is the single most
    /// consequential thing this report can say, and it is the claim a reader will act on without re-deriving.
    /// An `unreachable` floor is finding 336's state — no rung reaches the power at the horizon.
    /// </summary>
    public static string GateVerdict(double? storedAlphaStarAnn, double? recomputedAlphaStarAnn)
    {
        var wasUnreachable = storedAlphaStarAnn is { } sv && double.IsPositiveInfinity(sv);
        var nowUnreachable = recomputedAlphaStarAnn is { } rv && double.IsPositiveInfinity(rv);
        return (wasUnreachable, nowUnreachable) switch
        {
            (true, false) => "**THE GATE WOULD REOPEN.** The frozen curves put the detectability floor out of reach at this horizon (finding 336), and the recomputed ones do not — under these rules the arena can adjudicate a pre-registered claim again. This is a RECOMPUTED result: D117 clause 2 still requires the confirmation slice before it is treated as sign-off evidence.",
            (true, true) => "**The gate stays CLOSED** (finding 336). Detection may have improved, but no rung reaches the power within the horizon under these rules either, so the floor is still unreachable and no candidate is admissible. Reopening it needs a larger effect, a longer horizon under its own decision, or a different change — never a lowered bar.",
            (false, true) => "**WARNING — this change CLOSES the gate**: the frozen curves imply a reachable floor and the recomputed ones do not. That is an argument against the change, not a detail.",
            _ => "Both sides imply a reachable floor; this change moves its level rather than its existence.",
        };
    }

    private static string Sanitize(string name) =>
        new(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    private string Render(RecomputeReportModel r, RecomputeSpec spec, string today)
    {
        var isParity = spec.Overrides.Count == 0;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Recompute report — arena `{arena.Id}` — {today}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"*D106 recompute harness (MASTER §25), settlements D117. Run kind `{r.RunKind}`. **Report-only — no rows were written** (D117 clause 1).*");
        sb.AppendLine();
        sb.AppendLine("## Specification");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Spec:** `{r.SpecDescription}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Tier:** `{r.Tier}` — the inputs this change requires (§25.2 as amended by D117)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Subjects recomputed:** {r.SubjectsRecomputed}");
        sb.AppendLine();

        sb.AppendLine("## Known limits carried with this run");
        sb.AppendLine();
        sb.AppendLine("- **`GateOptions` is not as-of resolvable** (§25.1). `MinTrackDays` and the MDE parameters are bound from appsettings at composition, not versioned config rows, so reproducing this generation's promotions rests on the `Gate` block being unchanged since it ran — an assumption the harness cannot verify from the store.");
        if (r.ExcludedTruncationLimited.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Truncation-limited subjects EXCLUDED** (D117 clause 3, finding 338): {string.Join(", ", r.ExcludedTruncationLimited.Select(s => $"`{s}`"))}. Each retired during the generation, so it left the promotable set and stopped emitting rows; the sessions after that were never recorded and the \"would not have retired\" direction is not recomputable. Named rather than silently dropped.");
        }
        else
        {
            sb.AppendLine("- **No truncation-limited subjects**: nothing retired in this generation, so every subject's rows run full-length and the recompute is valid in both directions.");
        }
        sb.AppendLine();

        sb.AppendLine("## Artefacts");
        sb.AppendLine();
        sb.AppendLine("| Artefact | Stored | Recomputed | Differing |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var d in new[] { r.Statuses, r.Promotions, r.WouldReverts })
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{d.Artefact}` | {d.Stored} | {d.Recomputed} | **{d.Differing}** |");
        }
        sb.AppendLine();

        // How the promotion set changed, not merely how much — moved / gained / LOST mean opposite things.
        var shape = r.PromotionShape;
        if (shape.Moved + shape.Gained + shape.Lost > 0)
        {
            sb.AppendLine("### How the promotion set changed");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Moved** (same edge, different date): {shape.Moved}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Gained** (found by the new rule, never by the old): {shape.Gained}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **LOST** (found by the old rule, NOT by the new): {shape.Lost}");
            sb.AppendLine();
            sb.AppendLine(shape.Lost == 0
                ? "A LOST promotion is the one direction that argues AGAINST a rule change — an edge the arena used to find and would stop finding. There are none here, so the change is strictly additive on this artefact."
                : "**Every LOST subject is listed below in full, never sampled** — it is the direction that argues against the change, so it is the last thing an example cap may elide:");
            sb.AppendLine();
            foreach (var subject in shape.LostSubjects) sb.AppendLine(CultureInfo.InvariantCulture, $"- {subject}");
            sb.AppendLine();
        }

        if (r.Separation is { Horizons.Count: > 0 } sep) AppendSeparation(sb, sep);
        if (r.DetectionPower is { Rungs.Count: > 0 } dp) AppendDetectionPower(sb, dp);

        foreach (var d in new[] { r.Statuses, r.Promotions, r.WouldReverts }.Where(x => x.Examples.Count > 0))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Differences — `{d.Artefact}`");
            sb.AppendLine();
            foreach (var e in d.Examples) sb.AppendLine(CultureInfo.InvariantCulture, $"- {e}");
            sb.AppendLine();
        }

        sb.AppendLine("## Verdict");
        sb.AppendLine();
        if (isParity)
        {
            sb.AppendLine(r.ParityHolds
                ? "**`FX-RecomputeParity` HOLDS.** All three artefacts reproduce exactly under the current rules, so the harness reproduces this generation's machinery and may be used to score a rule change (D117 clause 2, still subject to the confirmation slice before anything is frozen)."
                : "**`FX-RecomputeParity` FAILED.** Per §25.3 the harness is **NOT USED for its purpose and generation 2 stands**. The equality is never relaxed to a tolerance: this routes to investigating which input is impure. It is a finding about the store, not a fixture to soften.");
        }
        else
        {
            sb.AppendLine("This run scored a **rule change**, so a difference is the product, not a fault. Before any recomputed number is treated as sign-off evidence, D117 clause 2 requires BOTH: `FX-RecomputeParity` holding under the current rules, AND a **confirmation slice** — a narrow `replay-calibrate --from/--to` under these corrected rules, agreeing with the harness over that same window. Parity exercises the UNCHANGED path; only the confirmation slice exercises this one, which is why one does not substitute for the other.");
        }
        sb.AppendLine();
        return sb.ToString();
    }
}
