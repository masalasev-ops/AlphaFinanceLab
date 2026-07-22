using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation;
using AlphaLab.Evaluation.Calibration;
using AlphaLab.Evaluation.Populations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>
/// The full `replay-calibrate` chain (checkpoint 4.8; DESIGN_IMPROVEMENTS §5): (1) the replay run
/// (ReplayRunner, 4.4/4.5); (2) curve build from the LEARN-period S3 paths (FR-42) — realistic AND the
/// naive comparator for the mandatory sensitivity section; (3) the C-1 detection-power sweep; (4) the
/// FR-41 per-regime decomposition; (5) the FX-Replay15y verification + KPIs; (6) the archived markdown
/// report; (7) unless --report-only, ONE transaction of append-only config INSERTs freezing the
/// calibrated values (D98 keys; finding 108's composite PK is what makes this implementable) + the
/// Calibration.ReportRef cross-reference. The Worker owns the whole chain (D59 sole writer).
/// </summary>
public sealed class CalibrationOrchestrator(
    IConfiguration configuration,
    ArenaOptions arena,
    ILoggerFactory loggerFactory)
{
    private const string Replay = "replay";
    private const int SensitivityMinTrackDays = 126;   // D64: divergence judged at t ≥ 126d

    private readonly ILogger _logger = loggerFactory.CreateLogger<CalibrationOrchestrator>();

    public async Task<int> RunAsync(string connectionString, ReplayRequest request, bool reportOnly, CancellationToken ct = default)
    {
        var runner = new ReplayRunner(configuration, arena, loggerFactory);
        var outcome = await runner.RunAsync(connectionString, request, ct).ConfigureAwait(false);
        if (outcome.StoppedEarly)
        {
            _logger.LogError("replay-calibrate: the replay stopped early ({Reason}) — no calibration on a partial window. " +
                             "Re-run the same command to resume.", outcome.StopReason);
            return 1;
        }

        var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
        using var db = new AlphaLabDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);

        var calibration = configuration.GetSection(CalibrationOptions.SectionName).Get<CalibrationOptions>() ?? new CalibrationOptions();
        var populations = configuration.GetSection(PopulationsOptions.SectionName).Get<PopulationsOptions>() ?? new PopulationsOptions();
        var gate = configuration.GetSection(GateOptions.SectionName).Get<GateOptions>() ?? new GateOptions();
        var verdicts = configuration.GetSection(VerdictsOptions.SectionName).Get<VerdictsOptions>() ?? new VerdictsOptions();
        var replayOptions = configuration.GetSection(ReplayOptions.SectionName).Get<ReplayOptions>() ?? new ReplayOptions();

        var specs = PlantCohorts.Build(calibration.Plant, PopulationFamilies.ForPhase3(populations));
        var vintage = BuildVintage(db, outcome, request, calibration, populations);

        // ---- (2) the curves, LEARN period only (FR-42) ----
        var edgeIds = PrimaryIds(specs, PlantKind.Edge);
        var noEdgeIds = PrimaryIds(specs, PlantKind.NoEdge);
        var naiveIds = PrimaryIds(specs, PlantKind.Naive);

        var edgePaths = CurveBuilder.PercentilePaths(db, edgeIds, request.LearnThrough).Values.ToList();
        var noEdgePaths = CurveBuilder.PercentilePaths(db, noEdgeIds, request.LearnThrough).Values.ToList();
        if (edgePaths.All(p => p.Count == 0) || noEdgePaths.All(p => p.Count == 0))
        {
            _logger.LogError("replay-calibrate: no S3 percentile paths in the learn period — the window is too short " +
                             "for a single evaluation cadence. Nothing to calibrate (fail closed).");
            return 1;
        }
        var pEdge = CurveBuilder.BuildEdge(edgePaths, "daily", gate.EvaluationCadenceDays, 2, 0.05, populations.Size, vintage);
        var pNoise = CurveBuilder.BuildNoise(noEdgePaths, "daily", gate.EvaluationCadenceDays, 2, 0.05, populations.Size, vintage);

        // The naive comparator (the PERMANENT sensitivity section — never the frozen curve).
        var naivePaths = CurveBuilder.PercentilePaths(db, naiveIds, request.LearnThrough).Values.ToList();
        S3Curve? naiveEdge = naivePaths.Any(p => p.Count > 0)
            ? CurveBuilder.BuildEdge(naivePaths, "daily", gate.EvaluationCadenceDays, 2, 0.05, populations.Size, vintage)
            : null;
        double? maxGap = null;
        if (naiveEdge is not null)
        {
            var gaps = pEdge.Knots
                .Where(k => k.T >= SensitivityMinTrackDays)
                .Select(k => Math.Abs(k.P - naiveEdge.At(k.T)))
                .ToList();
            if (gaps.Count > 0) maxGap = gaps.Max();
        }

        // ---- (3) the C-1 sweep, (4) FR-41, (5) verification ----
        var detectionPower = DetectionPowerCurves(db, specs, gate.EvaluationCadenceDays);
        new ReplayRegimeOutcomesWriter(db).Write(EvaluationStep.DefaultBenchmarkStrategyId);
        var verification = new ReplayVerification(db, gate, verdicts, replayOptions).Run(specs, request.LearnThrough, pNoise);

        // ---- (6) the archived report ----
        var generatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var reportDir = Path.Combine(configuration["Calibration:ReportDir"] ?? "docs/calibration", arena.Id);
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, $"{DateTime.UtcNow:yyyy-MM-dd}-calibration.md");

        var frozenKeys = reportOnly
            ? []
            : new List<string>
            {
                CalibratedKeys.PNoiseCurve("daily"), CalibratedKeys.PEdgeCurve("daily"),
                CalibratedKeys.DetectionPower, CalibratedKeys.S6AutoRetireEvals, CalibratedKeys.ReportRef,
            };

        var runIds = db.Runs.Where(r => r.RunKind == Replay && r.Status == "ok").Select(r => (long?)r.RunId).ToList();
        var report = CalibrationReport.Render(new CalibrationReportInputs(
            arena.Id, request.From, request.To, outcome.Watermark,
            runIds.Count > 0 ? runIds.Min() : null, runIds.Count > 0 ? runIds.Max() : null,
            request.LearnThrough, vintage.MembershipSource,
            calibration.Plant.SeedsPerPlant, populations.Size,
            pEdge, pNoise, naiveEdge, maxGap, calibration.Plant.SensitivityMaxGapPts,
            detectionPower, verification, frozenKeys), generatedAt);
        await File.WriteAllTextAsync(reportPath, report, ct).ConfigureAwait(false);
        var reportSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(report)));
        _logger.LogInformation("replay-calibrate: report archived at {Path} (sha256 {Sha}…).", reportPath, reportSha[..12]);

        // ---- (7) the freeze: ONE transaction, append-only version+1 INSERTs (never UPDATE) ----
        if (!reportOnly)
        {
            using var txn = db.Database.BeginTransaction();
            Freeze(db, CalibratedKeys.PNoiseCurve("daily"), pNoise.ToJson(), generatedAt);
            Freeze(db, CalibratedKeys.PEdgeCurve("daily"), pEdge.ToJson(), generatedAt);
            Freeze(db, CalibratedKeys.DetectionPower, JsonSerializer.Serialize(new
            {
                alphas_ann_pct = detectionPower.Keys.OrderBy(a => a).ToList(),
                curves = detectionPower.ToDictionary(
                    kv => kv.Key.ToString("0.##", CultureInfo.InvariantCulture),
                    kv => new
                    {
                        knots = kv.Value.PromotedByT.Select(k => new { t = k.T, p_promoted = k.P }).ToList(),
                        median_sessions_to_promotion = kv.Value.MedianSessionsToPromotion,
                        seeds = kv.Value.Seeds,
                    }),
                vintage,
            }), generatedAt);
            // The S6 patience knob exists as a row from the first freeze (finding 113: a survival-floor
            // failure recalibrates THIS — via a new version — never the plant). Initial = the Appendix-A 4.
            Freeze(db, CalibratedKeys.S6AutoRetireEvals,
                Evaluation.Monitor.OverfittingMonitor.AutoRetireConsecutiveSuspect.ToString(CultureInfo.InvariantCulture), generatedAt);
            Freeze(db, CalibratedKeys.ReportRef, JsonSerializer.Serialize(new { path = reportPath.Replace('\\', '/'), sha256 = reportSha }), generatedAt);
            txn.Commit();
            _logger.LogInformation("replay-calibrate: {Count} calibrated config row(s) frozen (append-only).", frozenKeys.Count);
        }

        if (!verification.NoFailures)
        {
            _logger.LogError("replay-calibrate: verification FAILURES present — read the report's check table.");
            return 1;
        }
        _logger.LogInformation("replay-calibrate: complete. AllGreen={AllGreen} (Insufficient checks are honest, not green).",
            verification.AllGreen);
        return 0;
    }

    private static void Freeze(AlphaLabDbContext db, string key, string valueJson, string changedOn)
    {
        var current = db.Config.Where(c => c.Key == key).AsEnumerable().OrderByDescending(c => c.Version).FirstOrDefault();
        db.Config.Add(new ConfigRow
        {
            Key = key,
            ValueJson = valueJson,
            Version = (current?.Version ?? 0) + 1,
            ChangedOn = changedOn,
            Reason = "Phase-4 replay calibration freeze (D56/D98; report archived in docs/calibration/).",
        });
        db.SaveChanges();
    }

    // The C-1 sweep: per edge alpha level, the fraction of seeds promoted by each evaluation index
    // (sessions grid) + the median sessions-to-promotion.
    private Dictionary<double, DetectionPowerCurve> DetectionPowerCurves(
        AlphaLabDbContext db, IReadOnlyList<PlantSpec> specs, int evalCadenceDays)
    {
        var sessions = db.Runs.Where(r => r.RunKind == Replay && r.Status == "ok")
            .OrderBy(r => r.AsOf).Select(r => r.AsOf).ToList();
        var result = new Dictionary<double, DetectionPowerCurve>();
        foreach (var level in specs.Where(s => s is { Kind: PlantKind.Edge, Family: "daily" }).GroupBy(s => s.AlphaAnnPct))
        {
            var ids = level.Select(s => s.StrategyId).ToList();
            var promotionSessions = new List<int>();
            foreach (var id in ids)
            {
                var first = db.GoLiveLog
                    .Where(g => g.RunKind == Replay && g.Promoted == id)
                    .Min(g => (string?)g.AsOf);
                if (first is null) continue;
                promotionSessions.Add(sessions.FindIndex(s => string.CompareOrdinal(s, first) >= 0) + 1);
            }
            var evaluations = Math.Max(1, sessions.Count / evalCadenceDays);
            var knots = new List<CurveKnot>();
            for (var i = 1; i <= evaluations; i++)
            {
                var t = i * evalCadenceDays;
                knots.Add(new CurveKnot(t, Math.Round(promotionSessions.Count(p => p <= t) / (double)ids.Count, 4)));
            }
            double? median = null;
            if (promotionSessions.Count > 0)
            {
                promotionSessions.Sort();
                median = promotionSessions[promotionSessions.Count / 2];
            }
            result[level.Key] = new DetectionPowerCurve(knots, median, ids.Count);
        }
        return result;
    }

    private static List<string> PrimaryIds(IReadOnlyList<PlantSpec> specs, PlantKind kind)
    {
        var of = specs.Where(s => s.Kind == kind && s.Family == "daily").ToList();
        if (of.Count == 0) return [];
        var primaryAlpha = kind == PlantKind.Edge ? of.Min(s => s.AlphaAnnPct) : of[0].AlphaAnnPct;
        return of.Where(s => s.AlphaAnnPct == primaryAlpha).Select(s => s.StrategyId).ToList();
    }

    private CurveVintage BuildVintage(
        AlphaLabDbContext db, ReplayOutcome outcome, ReplayRequest request,
        CalibrationOptions calibration, PopulationsOptions populations)
    {
        var sweep = new ConfigReadService(db).ResolveCurrent(HistoricalBackfillRunner.GateSweepConfigKey);
        var membershipSource = sweep is null
            ? "(no historical gate-sweep marker — a forward-store replay; see the coverage artifact requirements)"
            : $"fja05680 community CSV (Backfill.HistoricalGateSweep: {Truncate(sweep, 160)})";
        return new CurveVintage(
            arena.Id, request.From, request.To, outcome.Watermark, membershipSource,
            "realistic", calibration.Plant.SeedsPerPlant, populations.Size, request.LearnThrough,
            "pre-launch data carries residual survivorship bias (MASTER §13.4); replay flatters",
            "curves calibrated on S&P 500 as-of membership (D70); the forward universe is the S&P 100 slice until the widen");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
