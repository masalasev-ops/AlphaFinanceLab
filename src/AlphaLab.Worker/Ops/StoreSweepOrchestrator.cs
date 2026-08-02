using System.Globalization;
using System.Text;
using AlphaLab.Data;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>
/// The `store-sweep` chain (D120, findings 350/351): load each security's stored latest-version series →
/// run <see cref="StoredSeriesAudit"/> (the CURRENT gate + the two member-window detectors) → write the
/// archived markdown report with the paste-ready `Universe:Exclusions` recommendation. REPORT-ONLY, the
/// `replay-recompute` discipline (D117 clause 1): nothing here writes a row, a flag, or a config value —
/// the operator reviews the evidence and edits the exclusion list, so a detector false-positive can never
/// silently shrink the replay universe.
///
/// Exists because the store PREDATES its own guard: bars ingested 2026-07-15..23, the v1.9.41 R2 Reject
/// landed 2026-07-24 and screens fresh ingests only. This is the one sanctioned way to apply today's
/// standard to yesterday's corpus without deleting a bar (rule 3): the bars stay, the ROSTER forgets them
/// (finding 266's mechanism, the SUN precedent).
///
/// The report lands in the repo's TRACKED docs/calibration (the finding-276 anchoring, reused from
/// <see cref="CalibrationOrchestrator"/>).
/// </summary>
public sealed class StoreSweepOrchestrator(
    AlphaLabDbContext db,
    DataQualityOptions dataOptions,
    ArenaOptions arena,
    ILogger<StoreSweepOrchestrator> logger)
{
    public const string DefaultReportDir = "docs/calibration";

    public sealed record SweepRun(
        IReadOnlyList<SeriesAuditFinding> Findings,
        IReadOnlyList<SeriesAuditFinding> Recommended,
        string ReportPath);

    /// <summary><paramref name="today"/> is injected rather than read from the clock so a fixture can
    /// name the artefact deterministically (the RecomputeOrchestrator precedent).</summary>
    public SweepRun Run(string today, string? reportBaseDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(today);

        logger.LogInformation(
            "store-sweep: arena {Arena} — auditing the stored corpus with the current gate + member-window " +
            "detectors (D120). Report-only: no rows, no flags, no config are written.",
            arena.Id);

        var floor = db.Bars.Min(b => (string?)b.Date)
            ?? throw new InvalidOperationException("store-sweep: the store has no bars at all — nothing to audit.");

        var spellsBySecurity = db.IndexMembership.AsEnumerable()
            .GroupBy(m => m.SecurityId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<MembershipSpell>)g.OrderBy(m => m.AddedOn, StringComparer.Ordinal)
                      .Select(m => new MembershipSpell(m.AddedOn, m.RemovedOn)).ToList());

        var securities = db.Securities
            .Select(s => new { s.SecurityId, s.CurrentSymbol })
            .OrderBy(s => s.SecurityId)
            .ToList();

        var audit = new StoredSeriesAudit(new DataQualityGate(dataOptions), dataOptions);
        var findings = new List<SeriesAuditFinding>();
        var audited = 0;

        foreach (var sec in securities)
        {
            var bars = LoadLatestVersionBars(sec.SecurityId);
            if (bars.Count == 0 && !spellsBySecurity.ContainsKey(sec.SecurityId)) continue;

            var actions = LoadLatestVersionActions(sec.SecurityId);
            var spells = spellsBySecurity.GetValueOrDefault(sec.SecurityId, []);

            var finding = audit.Audit(new SeriesAuditInput(
                sec.SecurityId, sec.CurrentSymbol, bars, actions, spells, floor));
            audited++;

            if (finding.RecommendExclusion || finding.GateWarns > 0 || finding.SpellsWithNoBars > 0)
            {
                findings.Add(finding);
            }
        }

        var recommended = findings.Where(f => f.RecommendExclusion)
            .OrderByDescending(f => f.GateRejects)
            .ThenByDescending(f => f.DollarVolumeBreachWindows)
            .ToList();

        var baseDir = CalibrationOrchestrator.ResolveReportBaseDir(
            reportBaseDir ?? DefaultReportDir, CalibrationOrchestrator.FindRepoRoot());
        var dir = Path.Combine(baseDir, arena.Id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{today}-store-sweep.md");
        File.WriteAllText(path, Render(findings, recommended, audited, floor, today), new UTF8Encoding(false));

        logger.LogWarning(
            "store-sweep: {Audited} securities audited; {Recommended} recommended for Universe:Exclusions " +
            "({Rejects} with impossible prints, {Volume} with member-volume breaches, {Yield} with impossible " +
            "dividend yields); {NoBars} membership spell(s) have no stored bars. Report: {Path}",
            audited, recommended.Count,
            recommended.Count(f => f.GateRejects > 0),
            recommended.Count(f => f.DollarVolumeBreachWindows > 0),
            recommended.Count(f => f.DividendYieldBreaches > 0),
            findings.Sum(f => f.SpellsWithNoBars), path);

        return new SweepRun(findings, recommended, path);
    }

    /// <summary>One security's bars, latest version per date, date-ordered — the same resolution every
    /// reader uses (SCHEMA read rule), shaped for the gate.</summary>
    private List<EodBar> LoadLatestVersionBars(long securityId)
    {
        return db.Bars.Where(b => b.SecurityId == securityId)
            .AsEnumerable()
            .GroupBy(b => b.Date)
            .Select(g => g.OrderByDescending(b => b.Version).First())
            .OrderBy(b => b.Date, StringComparer.Ordinal)
            .Select(b => new EodBar(b.Date, b.Open, b.High, b.Low, b.Close, b.AdjClose, b.Volume))
            .ToList();
    }

    /// <summary>One security's actions, latest version per (type, effective_date) — the D76 read rule.</summary>
    private List<Data.Entities.CorporateActionRow> LoadLatestVersionActions(long securityId)
    {
        return db.CorporateActions.Where(a => a.SecurityId == securityId)
            .AsEnumerable()
            .GroupBy(a => (a.Type, a.EffectiveDate))
            .Select(g => g.OrderByDescending(a => a.Version).First())
            .OrderBy(a => a.EffectiveDate, StringComparer.Ordinal)
            .ToList();
    }

    private string Render(
        IReadOnlyList<SeriesAuditFinding> findings,
        IReadOnlyList<SeriesAuditFinding> recommended,
        int audited,
        string floor,
        string today)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Store sweep — {arena.Id}, {today} (D120)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"*The stored corpus, audited by the CURRENT data-quality gate plus the two member-window detectors " +
            $"(findings 350/351). {audited} securities audited; store coverage floor {floor}. Report-only: " +
            $"remediation is the operator adding the recommended symbols to `Universe:Exclusions` " +
            $"(finding 266's roster deny-list — the bars stay, the roster forgets them, rule 3).*");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Thresholds: single-event dividend yield ≥ ×{dataOptions.SweepMaxSingleDividendYield.ToString(CultureInfo.InvariantCulture)}; " +
            $"member 63-session median dollar volume < ${dataOptions.SweepMinMemberDollarVolume.ToString("0", CultureInfo.InvariantCulture)}; " +
            $"gate bound ×{dataOptions.MaxSingleDayPriceFactor.ToString(CultureInfo.InvariantCulture)}.");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"## Recommended for `Universe:Exclusions` — {recommended.Count} securities");
        sb.AppendLine();
        if (recommended.Count == 0)
        {
            sb.AppendLine("None — no stored series shows positive evidence of being the wrong company.");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("| symbol | id | impossible prints (R2) | member-volume breach windows | impossible dividend yields | worst evidence |");
            sb.AppendLine("|---|---:|---:|---:|---:|---|");
            foreach (var f in recommended)
            {
                var evidence = f.RejectSamples.FirstOrDefault() ?? f.WorstVolumeDetail ?? f.WorstDividendDetail ?? "";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{f.Symbol}` | {f.SecurityId} | {f.GateRejects} | {f.DollarVolumeBreachWindows} | {f.DividendYieldBreaches} | {Escape(evidence)} |");
            }
            sb.AppendLine();
            sb.AppendLine("Paste-ready (append to the existing list — do NOT drop `SUN`):");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"\"Exclusions\": [ {string.Join(", ", recommended.Select(f => $"\"{f.Symbol}\""))} ]");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"## Membership spells with no stored bars — {findings.Sum(f => f.SpellsWithNoBars)} spell(s)");
        sb.AppendLine();
        sb.AppendLine("*Coverage, not exclusion: nothing was ingested, so there is nothing to quarantine. These are " +
            "members the replay CANNOT price for the listed spell (the NCC shape: the vendor file is entirely a " +
            "later recycled listing). The 7.9M `missing_bar` warns record this per-day; this is the per-security rollup.*");
        sb.AppendLine();
        var withGaps = findings.Where(f => f.SpellsWithNoBars > 0).OrderBy(f => f.Symbol, StringComparer.Ordinal).ToList();
        if (withGaps.Count > 0)
        {
            sb.AppendLine("| symbol | id | bareless spells | also recommended for exclusion? |");
            sb.AppendLine("|---|---:|---:|---|");
            foreach (var f in withGaps)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{f.Symbol}` | {f.SecurityId} | {f.SpellsWithNoBars} | {(f.RecommendExclusion ? "yes" : "no")} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## What this settles and what it does not");
        sb.AppendLine();
        sb.AppendLine("- A recommended exclusion removes a FICTIONAL series from the roster, not a real loser: the " +
            "companies these tickers belonged to have their true history simply absent on this data tier. The " +
            "survivorship caveat goes in the calibration report (D49 discipline), not silently.");
        sb.AppendLine("- Exclusion changes NOTHING retroactively: generation-1 curves already inhaled these prints " +
            "(finding 348's contamination). The clean numbers come from the generation-2 re-run on the excluded " +
            "roster, never from patching stored curves.");
        sb.AppendLine("- Fresh ingests are already protected by the v1.9.41 gate; this sweep exists for the corpus " +
            "that predates it, and re-running it after any future bulk backfill is cheap and sanctioned.");

        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);
}
