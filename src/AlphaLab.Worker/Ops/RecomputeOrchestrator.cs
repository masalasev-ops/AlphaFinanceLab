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
