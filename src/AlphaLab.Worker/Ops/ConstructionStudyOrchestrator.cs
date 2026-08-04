using System.Diagnostics;
using System.Globalization;
using System.Text;
using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Core.Signals;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Construction;
using AlphaLab.Evaluation.Metrics;
using AlphaLab.Evaluation.Power;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>What a construction study was asked to measure (Phase 5.5, FR-47).</summary>
public sealed record ConstructionStudyRequest(
    string From, string To, double? TailFraction = null, IReadOnlyList<double>? BorrowBpPerYear = null);

/// <summary>
/// The `construction-study` chain (D123, Phase 5.5): for each registered <see cref="ISignal"/>, build a
/// monthly-rebalanced top tail and bottom tail over the stored history and report the tracking error —
/// and therefore the detectability floor — under a LONG-ONLY and a LONG-SHORT construction.
///
/// REPORT-ONLY, on the `store-sweep` / `replay-recompute` discipline (D117 clause 1): it writes one
/// markdown artefact under docs/calibration and never a row, a flag or a config value. It therefore
/// carries no <see cref="SoleWriterGate"/> and takes no transaction. Nothing it produces is read at
/// runtime by anything.
///
/// WHY IT EXISTS. The floor is ZSum·TE/√H, so tracking error decides what this arena can adjudicate at
/// all. Generation 2 froze the band at roughly [7 %, 32 %]/yr under the only construction the lab has
/// ever run — long-only, measured against a broad benchmark. A dollar-neutral book carries far less
/// market risk, so its TE should be lower and its floor with it. Whether the difference is large enough
/// to be worth building shorting for is a MEASUREMENT, and this verb is the instrument. A negative
/// answer is as publishable as a positive one and saves weeks.
///
/// TWO RAILS, both restated in the rendered report because that is where a future reader meets them:
///  • D91 descriptive-only. Informing a BUILD decision is not the allocator, a gate, sizing or
///    eligibility. This namespace is deliberately not among the consumer directories `ci.ps1` scans and
///    must never be added to them.
///  • The output may NEVER set a pre-registered `expected_effect_ann` (rule 16 / D52). Choosing the
///    number you then pre-register by looking at measured data is exactly what pre-registration exists
///    to prevent.
/// </summary>
public sealed class ConstructionStudyOrchestrator(
    IConfiguration configuration,
    ArenaOptions arena,
    ILoggerFactory loggerFactory)
{
    public const string DefaultReportDir = "docs/calibration";

    private readonly ILogger _logger = loggerFactory.CreateLogger<ConstructionStudyOrchestrator>();

    public sealed record StudyRun(ConstructionStudyResult Result, string ReportPath, TimeSpan Elapsed);

    public StudyRun Run(
        string connectionString,
        ConstructionStudyRequest request,
        string today,
        string? reportBaseDir = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(today);

        using var arenaScope = _logger.BeginArenaScope(arena);
        var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
        DbPathResolver.RequireAbsoluteStorePath(resolved);

        var stopwatch = Stopwatch.StartNew();
        using var db = new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);

        // Same fail-closed schema rule as every other verb: a store mid-migration would be read with
        // the wrong shape and the study would report numbers off a table that is about to change.
        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"The store has {pending.Count} pending migration(s) ({string.Join(", ", pending)}) — " +
                $"run pwsh tools/migrate.ps1 -Arena {arena.Id} first (snapshot-first, rule 14).");
        }

        var gate = configuration.GetSection(GateOptions.SectionName).Get<GateOptions>() ?? new GateOptions();
        var costsOptions = configuration.GetSection(CostsOptions.SectionName).Get<CostsOptions>() ?? new CostsOptions();
        var universe = configuration.GetSection(UniverseOptions.SectionName).Get<UniverseOptions>() ?? new UniverseOptions();

        var options = new ConstructionStudyOptions
        {
            TailFraction = request.TailFraction ?? new ConstructionStudyOptions().TailFraction,
            BorrowBpPerYear = request.BorrowBpPerYear ?? new ConstructionStudyOptions().BorrowBpPerYear,
            HorizonSessions = gate.EvaluationCadenceDays,
        };

        var watermark = ResolveWatermark(db);
        var proxy = ResolveMarketProxy(db, watermark);

        var calendar = new CalendarService(db);
        var sessions = calendar.SessionsBetween(ParseDate(request.From), ParseDate(request.To)).ToList();
        if (sessions.Count < 2)
        {
            throw new InvalidOperationException(
                $"Fewer than two trading sessions in [{request.From}, {request.To}] — nothing to measure " +
                "(the calendar is unseeded for the window, or the dates are outside the seeded range).");
        }

        _logger.LogInformation(
            "construction-study: arena {Arena} — {Count} session(s) {From}..{To} at watermark {Watermark}; " +
            "tail {Tail:P0}, borrow [{Borrow}] bp/yr. REPORT-ONLY: no rows, no flags, no config are written.",
            arena.Id, sessions.Count, sessions[0], sessions[^1], watermark,
            options.TailFraction, string.Join(", ", options.BorrowBpPerYear));

        var bars = new BarReadService(db);
        var panel = LoadPanel(bars, sessions, watermark, ct);

        // Membership resolves through the EXCLUSION-scoped read the replay uses (D97/D109), never the
        // forward slice-scoped one — the study describes market history, and scoping it to the sp100
        // launch slice would measure a different universe than the report claims.
        var membership = new ExclusionScopedMembershipRead(new IndexMembershipReadService(db), db, universe);

        var engine = new ConstructionStudyEngine(
            panel,
            // The SCORING path uses the real BarFeatureView — the one class that owns the watermark rule
            // (rule 4). The study deliberately does not implement a second point-in-time view.
            asOf => new BarFeatureView(bars, calendar, asOf, watermark, costsOptions),
            asOf => membership.MembersAsOf(Iso(asOf)).Select(id => new SecurityId(id)).ToList(),
            new CostModel(costsOptions),
            gate,
            options);

        // ONE pass over the sessions for all seven signals — see MeasureAll: a per-signal pass would
        // rebuild the point-in-time view at every rebalance seven times over and turn a few minutes of
        // windowed bar reads into millions of them.
        var context = new SignalContext(proxy);
        var measurements = engine.MeasureAll(SignalRegistry.V1, context, ct);
        foreach (var m in measurements)
        {
            _logger.LogInformation(
                "construction-study {Signal}: {Rebalances} rebalance(s), tail {Tail:F1} of {Scored:F1} scored; " +
                "long-only TE {TeLo:P2} floor {FlLo:P2}; long-short TE {TeLs:P2} floor {FlLs:P2}.",
                m.SignalId, m.Rebalances, m.MeanTailSize, m.MeanScoredNames,
                m.LongOnly.TrackingErrorAnn, m.LongOnly.FloorAnn,
                m.LongShort.TrackingErrorAnn, m.LongShort.FloorAnn);
        }

        var result = new ConstructionStudyResult(
            arena.Id, watermark, Iso(sessions[0]), Iso(sessions[^1]), sessions.Count,
            gate.DetectabilityHorizonYears, MdeCalculator.ZSum(gate.Confidence, gate.Power),
            options, ResolveControl(db, gate), measurements);

        var baseDir = CalibrationOrchestrator.ResolveReportBaseDir(
            reportBaseDir ?? DefaultReportDir, CalibrationOrchestrator.FindRepoRoot());
        var dir = Path.Combine(baseDir, arena.Id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{today}-construction-study.md");
        File.WriteAllText(path, Render(result, today), new UTF8Encoding(false));

        stopwatch.Stop();
        _logger.LogInformation(
            "construction-study complete in {Elapsed} (MEASURED on this machine). Report: {Path}",
            stopwatch.Elapsed, path);

        return new StudyRun(result, path, stopwatch.Elapsed);
    }

    /// <summary>
    /// The adjusted-close panel, one date-major cross-section per session (D78) through the SAME
    /// <see cref="IBarReadService"/> every other reader uses. Reusing it rather than writing a bulk
    /// query means the study's "latest version ≤ watermark" resolution cannot drift from the lab's.
    /// </summary>
    private AdjClosePanel LoadPanel(
        IBarReadService bars, IReadOnlyList<DateOnly> sessions, string watermark, CancellationToken ct)
    {
        var byId = new Dictionary<long, double?[]>();
        var priced = 0L;

        for (var i = 0; i < sessions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var bar in bars.GetCrossSection(Iso(sessions[i]), watermark))
            {
                if (bar.AdjClose is not { } adj || !double.IsFinite(adj) || adj <= 0) continue;
                if (!byId.TryGetValue(bar.SecurityId, out var row))
                {
                    row = new double?[sessions.Count];
                    byId[bar.SecurityId] = row;
                }
                row[i] = adj;
                priced++;
            }

            if ((i + 1) % 500 == 0)
            {
                _logger.LogInformation(
                    "construction-study: panel {Done}/{Total} session(s) loaded ({Securities} securities).",
                    i + 1, sessions.Count, byId.Count);
            }
        }

        if (byId.Count == 0)
        {
            throw new InvalidOperationException(
                $"No adjusted closes visible at watermark {watermark} over the requested window — " +
                "nothing to measure (fail closed).");
        }

        _logger.LogInformation(
            "construction-study: panel loaded — {Securities} securities, {Priced} priced (security, session) cells.",
            byId.Count, priced);
        return new AdjClosePanel(sessions, byId);
    }

    /// <summary>
    /// The control: the arena's own σ_LR, read the way <c>DetectabilityGate</c> reads it (median of the
    /// most recent 50 <c>power_reports</c>, forward first, replay as the calibration-vintage fallback).
    /// Null when nothing is estimable yet, which is an honest answer and never a zero.
    /// </summary>
    private static ControlBaseline? ResolveControl(AlphaLabDbContext db, GateOptions gate)
    {
        (double? Sigma, int N) Median(string runKind)
        {
            var sigmas = db.PowerReports
                .Where(p => p.RunKind == runKind && p.SigmaLr > 0)
                .OrderByDescending(p => p.AsOf)
                .Select(p => p.SigmaLr)
                .Take(50)
                .ToList();
            if (sigmas.Count == 0) return (null, 0);
            sigmas.Sort();
            return (sigmas[sigmas.Count / 2], sigmas.Count);
        }

        var (sigma, n) = Median("live");
        var source = "forward (power_reports run_kind='live')";
        if (sigma is null)
        {
            (sigma, n) = Median("replay");
            source = "replay generation (power_reports run_kind='replay') — the calibration-vintage estimate";
        }
        if (sigma is not { } s) return null;

        var te = s * Math.Sqrt(MetricsConstants.TradingDaysPerYear);
        var floor = MdeCalculator.ZSum(gate.Confidence, gate.Power) * s
                    * MetricsConstants.TradingDaysPerYear
                    / Math.Sqrt(Math.Max(1, gate.DetectabilityHorizonYears) * MetricsConstants.TradingDaysPerYear);
        return new ControlBaseline(source, n, s, te, floor);
    }

    /// <summary>MAX(observed_at) over the versioned input tables — the same rule the replay freezes.</summary>
    private static string ResolveWatermark(AlphaLabDbContext db)
    {
        var barMax = db.Bars.Max(b => (string?)b.ObservedAt);
        var caMax = db.CorporateActions.Max(c => (string?)c.ObservedAt);
        var max = string.CompareOrdinal(barMax, caMax) >= 0 ? barMax : caMax ?? barMax;
        return max ?? throw new InvalidOperationException(
            "The store has no bars — nothing to measure. Run the D70 historical backfill first (fail closed).");
    }

    /// <summary>The market proxy `resmom`/`bab` regress against, as-of the watermark (D96). A null proxy
    /// is legitimate but consequential here — those two scorers then emit NOTHING, so the study would
    /// silently report five signals instead of seven. Warned loudly for that reason.</summary>
    private SecurityId? ResolveMarketProxy(AlphaLabDbContext db, string watermark)
    {
        var id = new ConfigReadService(db).ResolveLongAsOf(RegimeProxyIngestion.ProxyConfigKey, watermark);
        if (id is null)
        {
            _logger.LogWarning(
                "construction-study: no market proxy resolved ({Key}) — resmom:L252 and bab:L252 will score " +
                "nothing and their rows will read as zero rebalances. That is a data gap, not a result.",
                RegimeProxyIngestion.ProxyConfigKey);
        }
        return id is { } v ? new SecurityId(v) : null;
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDate(string iso) =>
        DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- the artefact ----

    private static string Render(ConstructionStudyResult r, string today)
    {
        var sb = new StringBuilder();
        var o = r.Options;

        sb.AppendLine(CultureInfo.InvariantCulture, $"# Construction study — {r.ArenaId}, {today} (D123, Phase 5.5)");
        sb.AppendLine();
        sb.AppendLine("**The question: can this arena adjudicate a realistic edge, or only an implausible one?**");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"The detectability floor is `ZSum x TE / sqrt(H)`, so TRACKING ERROR decides what is adjudicable at all. " +
            $"This measures TE under two constructions of the same signal, over the same universe, the same " +
            $"rebalance and the same costs — the only difference is the construction. " +
            $"ZSum = {r.ZSum.ToString("0.0000", CultureInfo.InvariantCulture)} (Gate.Confidence/Gate.Power), " +
            $"H = {r.HorizonYears} year(s) (Gate.DetectabilityHorizonYears).");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"*Window {r.FromSession}..{r.ToSession} ({r.Sessions} sessions) at watermark `{r.Watermark}`. " +
            $"Tail fraction {o.TailFraction.ToString("P0", CultureInfo.InvariantCulture)}; monthly rebalance " +
            // "C0" under InvariantCulture emits the GENERIC currency sign (¤), not a dollar — the invariant
            // culture deliberately has no currency of its own. Format the number and name the unit.
            $"(first session of each calendar month); book ${o.Notional.ToString("N0", CultureInfo.InvariantCulture)} USD; " +
            $"NW lag from a {o.HorizonSessions}-session holding period.*");
        sb.AppendLine();

        sb.AppendLine("## What this report is NOT");
        sb.AppendLine();
        sb.AppendLine("- **It is not a backtest and no strategy is being proposed.** It measures a PROPERTY OF A " +
            "CONSTRUCTION — how noisy the active series is — not whether any signal makes money.");
        sb.AppendLine("- **Nothing here may set a pre-registered `expected_effect_ann` (rule 16 / D52).** Choosing " +
            "the number you then pre-register by looking at measured data is exactly what pre-registration " +
            "exists to prevent. This answers \"which construction?\", never \"what should I claim?\".");
        sb.AppendLine("- **The Signal Library stays descriptive-only (D91).** Informing a BUILD decision is not " +
            "the allocator, a gate, sizing or eligibility. No output of this study is read at runtime.");
        sb.AppendLine("- **Borrow cost is an ASSUMPTION, not a measurement.** The D43 model has no borrow term " +
            "and this arena buys no borrow data. The 0 bp column is the optimistic bound: a construction " +
            "that fails to lower the floor even when borrowing is free fails for a reason no borrow data " +
            "would rescue.");
        sb.AppendLine();

        if (r.Control is { } c)
        {
            sb.AppendLine("## Control — what the arena measures for itself today");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| source | n | sigma_LR (daily) | TE (ann) | floor at H={r.HorizonYears}y |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Source} | {c.Samples} | {Fmt(c.SigmaLrDaily, 6)} | {Pct(c.TrackingErrorAnn)} | {Pct(c.FloorAnn)} |");
            sb.AppendLine();
            sb.AppendLine("*Read from the same `power_reports` sigma the admission gate's analytic floor uses, so " +
                "the comparison below is against a measured number rather than one quoted from a document.*");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Control — unavailable");
            sb.AppendLine();
            sb.AppendLine("*No `power_reports` sigma is estimable, so the arena has no measured baseline to compare " +
                "against. Reported as absent rather than defaulted to zero.*");
            sb.AppendLine();
        }

        sb.AppendLine("## The measurement");
        sb.AppendLine();
        sb.AppendLine("| signal | family | rebal | tail | scored | construction | TE (ann) | floor | gross eff | cost drag | net eff |");
        sb.AppendLine("|---|---|---:|---:|---:|---|---:|---:|---:|---:|---|");
        foreach (var m in r.Signals)
        {
            foreach (var leg in new[] { m.LongOnly, m.LongShort })
            {
                var nets = string.Join("<br>", leg.NetEffects.Select(n =>
                    $"{Pct(n.NetEffectAnn)} @ {n.BorrowBpPerYear.ToString("0", CultureInfo.InvariantCulture)}bp"));
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{m.SignalId}` | {m.Family} | {m.Rebalances} | {m.MeanTailSize.ToString("F1", CultureInfo.InvariantCulture)} | " +
                    $"{m.MeanScoredNames.ToString("F1", CultureInfo.InvariantCulture)} | {leg.Construction} | " +
                    $"{Pct(leg.TrackingErrorAnn)} | {Pct(leg.FloorAnn)} | {Pct(leg.GrossEffectAnn)} | " +
                    $"{Pct(leg.CostDragAnn)} | {nets} |");
            }
        }
        sb.AppendLine();

        // The headline. NOT the floor ratio — see the explanation below, which is the correction this
        // study forced on its own design.
        sb.AppendLine("## The decision number — information ratio and years-to-detect");
        sb.AppendLine();
        sb.AppendLine("**Do NOT compare the two floors above.** A long-short book is roughly 2x leverage on the " +
            "same cross-sectional bet: it scales the tracking error AND the effect together. The floor rises " +
            "with TE, but so does the effect that has to clear it, so the comparison says nothing. The " +
            "t-statistic is `IR x sqrt(T)`, so detectability depends on the INFORMATION RATIO alone — and " +
            "`years-to-detect = (ZSum / IR)^2` is the quantity that IS comparable across constructions.");
        sb.AppendLine();
        // The single most useful number in the report: invert years-to-detect at the gate horizon and you
        // get the information ratio a strategy must SUSTAIN to be adjudicable here at all. It is a
        // property of the horizon and the confidence/power pair alone — no measurement enters it — which
        // is what makes it the bar every row below is read against.
        var requiredIr = Fmt(r.ZSum / Math.Sqrt(Math.Max(1, r.HorizonYears)), 3);
        sb.AppendLine(
            $"**The bar: at H = {r.HorizonYears} years this arena can only adjudicate a strategy whose " +
            $"active-return information ratio is at least `ZSum/sqrt(H)` = {requiredIr}, sustained.** " +
            $"That follows from the horizon and the confidence/power pair alone — no measurement enters " +
            $"it. Read every row below against it.");
        sb.AppendLine();
        sb.AppendLine("| signal | IR long-only | IR long-short | IR gain | yrs to detect (LO) | yrs (LS) | reading |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var m in r.Signals)
        {
            // The 0 bp rows: the optimistic bound, and the like-for-like comparison (long-only borrows
            // nothing, so its only row is 0 bp).
            var lo = m.LongOnly.NetEffects.FirstOrDefault();
            var ls = m.LongShort.NetEffects.FirstOrDefault();
            if (lo is null || ls is null) continue;

            var gain = lo.InformationRatio > 0 ? ls.InformationRatio / lo.InformationRatio : double.NaN;
            var reading = !double.IsFinite(ls.YearsToDetect) ? "no measured effect"
                : ls.YearsToDetect > lo.YearsToDetect ? "long-short is WORSE here"
                : ls.YearsToDetect <= r.HorizonYears ? "**resolvable inside the gate horizon**"
                : gain >= 1.5 ? "materially faster, still beyond the horizon"
                : "no material gain — leverage, not information";

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| `{m.SignalId}` | {Fmt(lo.InformationRatio, 3)} | {Fmt(ls.InformationRatio, 3)} | " +
                $"{(double.IsFinite(gain) ? gain.ToString("F2", CultureInfo.InvariantCulture) + "x" : "-")} | " +
                $"{Years(lo.YearsToDetect)} | {Years(ls.YearsToDetect)} | {reading} |");
        }
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"*An IR gain near 1.00x means the construction bought LEVERAGE, not information — the same bet " +
            $"twice the size, detectable no sooner. A gain meaningfully above 1.00x is the only thing that " +
            $"would justify building shorting. Compare years-to-detect against the gate horizon of " +
            $"{r.HorizonYears} year(s).*");
        sb.AppendLine();
        sb.AppendLine("*The banding is a READING AID, not a threshold the code enforces: no verdict, gate or " +
            "config anywhere consumes it. The decision is the operator's, made on the numbers and the rule " +
            "text — never on which answer is more convenient.*");
        sb.AppendLine();
        sb.AppendLine("### Borrow sensitivity (long-short only)");
        sb.AppendLine();
        sb.AppendLine("| signal | " + string.Join(" | ", r.Options.BorrowBpPerYear.Select(b =>
            $"IR @ {b.ToString("0", CultureInfo.InvariantCulture)}bp")) + " | verdict flips? |");
        sb.AppendLine("|---|" + string.Concat(r.Options.BorrowBpPerYear.Select(_ => "---:|")) + "---|");
        foreach (var m in r.Signals)
        {
            var irs = m.LongShort.NetEffects.Select(n => n.InformationRatio).ToList();
            var loIr = m.LongOnly.NetEffects.FirstOrDefault()?.InformationRatio ?? 0.0;
            // The question the two assumptions exist to answer: does the conclusion depend on a number
            // this arena is guessing at? If not, the missing borrow data does not matter here.
            var flips = irs.Count > 1 && irs.Any(x => x > loIr) && irs.Any(x => x <= loIr);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| `{m.SignalId}` | {string.Join(" | ", irs.Select(x => Fmt(x, 3)))} | " +
                $"{(flips ? "**YES — the answer depends on borrow data this arena does not have**" : "no")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Caveats a reader must carry");
        sb.AppendLine();
        sb.AppendLine("- **Tracking error is NW-corrected** (sigma_LR, not the naive standard deviation). An " +
            "autocorrelated active series gives sigma_LR > sigma_naive, so these floors are LARGER than a " +
            "naive TE would imply. A study arguing for a lower floor must not pick the estimator that flatters it.");
        sb.AppendLine("- **Cost is a drag on the mean, never folded into the series TE is measured from.** A " +
            "monthly cost lands as a lump on twelve days a year; charging it to the series would add variance " +
            "that is an artefact of the rebalance calendar rather than a property of the construction.");
        sb.AppendLine("- **The floor here carries no Bonferroni trials haircut**, deliberately — that haircut is a " +
            "property of how many candidates the arena has registered, and including it would make the two " +
            "constructions differ by the trials count as well as by their tracking error.");
        sb.AppendLine("- **The benchmark is the SCORED set, not the eligible pool** (finding 294's rule). A " +
            "benchmark holding names the signal could not score would fold \"thin-history names behaved " +
            "differently\" into what this calls the signal's active return.");
        sb.AppendLine("- **Survivorship and the stored corpus.** The universe resolves through the D97/D109 " +
            "exclusion-scoped as-of membership, so the D120 sweep's exclusions apply; the D49 community-CSV " +
            "survivorship caveat still rides on every historical statement this arena makes.");
        sb.AppendLine("- **A signal showing zero rebalances is a DATA GAP, not a result** — most likely a missing " +
            "market proxy, which leaves `resmom:L252` and `bab:L252` scoring nothing at all.");

        return sb.ToString();
    }

    private static string Pct(double v) =>
        double.IsFinite(v) ? v.ToString("P2", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>Years-to-detect, or "never" when the measured effect is zero. Spelled out rather than
    /// printed as a huge finite number, which would invite a reader to think a longer study would do it.</summary>
    private static string Years(double v) =>
        !double.IsFinite(v) ? "never" : v.ToString("F1", CultureInfo.InvariantCulture);

    private static string Fmt(double v, int dp) =>
        double.IsFinite(v) ? v.ToString("F" + dp.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : "n/a";
}
