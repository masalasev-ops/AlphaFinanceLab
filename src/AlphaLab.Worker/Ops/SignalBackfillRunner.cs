using System.Diagnostics;
using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Signals;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Signals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>What a signal-IC backfill was asked to do (FR-45).</summary>
public sealed record SignalBackfillRequest(string From, string To);

/// <summary>What one backfill pass did. <paramref name="Elapsed"/> is MEASURED, never promised.</summary>
/// <param name="SessionsSkippedComplete">Days whose full (signal × horizon) set was already stored and
/// were therefore skipped WITHOUT scoring — the figure that makes resumability real rather than
/// nominal (finding 300). A resumed run should report most of its window here.</param>
public sealed record SignalBackfillOutcome(
    int SessionsPlanned, int SessionsGraded, int GradesWritten, int GradesAlreadyPresent, TimeSpan Elapsed,
    int SessionsSkippedComplete = 0);

/// <summary>
/// Thrown when the trend-flag significance levels are not yet pinned. Its own type so the refusal is
/// testable as a contract rather than by matching a log string.
/// </summary>
public sealed class SignalThresholdsNotPinnedException(IReadOnlyList<string> missingKeys)
    : InvalidOperationException(
        $"The Signal-Library trend thresholds are not pinned: {string.Join(", ", missingKeys)}. " +
        "Pin them at build checkpoint 4.5.2 BEFORE any grade row exists (D108) — pinning them after a " +
        "20-year backfill would be choosing thresholds by looking at the answer. Refusing to grade.")
{
    public IReadOnlyList<string> MissingKeys { get; } = missingKeys;
}

/// <summary>
/// The FR-45 full-history signal-IC backfill (D91, MASTER §24). Runs as a WORKER verb because the
/// Worker is the sole writer (D59) — `tools/Backfill` is sanctioned only as the Phase-1 bootstrap
/// writer, and `signal_ic` is not bootstrap data.
///
/// THE PIN GUARD IS THE POINT (D108). This REFUSES to grade a single day while either significance
/// level is absent from `config`, fail closed with the missing keys named. That refusal — a check on
/// DB state, which no appsettings edit can bypass — is what makes "pinned before the first grade row"
/// a shape the code cannot violate rather than a discipline a comment asks for.
///
/// NOT A REPLAY GENERATION (D95): no `runs` row, no generation, and `signal_ic` carries no `run_kind`.
/// A grade is a property of a signal and a date; there is one market history to grade.
///
/// RESUMABLE + IDEMPOTENT with no cursor table: the already-written `(signal_id, as_of, horizon_days)`
/// rows ARE the progress marker (the `HistoricalBackfill` "expected vs stored" shape — INCLUDING its
/// ORDERING, which is the half that did not survive the first port: finding 300). The coverage check
/// runs BEFORE scoring, and the unit it skips is the DAY: a day whose FULL (signal × horizon) set is
/// stored is skipped WITHOUT being graded (`SessionsSkippedComplete` counts those days), while a
/// PARTIALLY graded day is re-graded in full so its missing pairs can land — `Persist` then discards
/// the pairs already there, since the day-level check is the optimisation and the persist-time skip is
/// the correctness rule. A completed run re-run therefore scores nothing and writes nothing.
/// </summary>
public sealed class SignalBackfillRunner(
    IConfiguration configuration,
    ArenaOptions arena,
    ILoggerFactory loggerFactory)
{
    /// <summary>The two versioned config rows D108 pins. Named here so the refusal can report them.</summary>
    public const string DecayAlphaKey = "SignalLibrary.TrendDecayAlpha";
    public const string GoneAlphaKey = "SignalLibrary.TrendGoneAlpha";

    private readonly ILogger _logger = loggerFactory.CreateLogger<SignalBackfillRunner>();

    public async Task<SignalBackfillOutcome> RunAsync(
        string connectionString, SignalBackfillRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);

        using var arenaScope = _logger.BeginArenaScope(arena);
        var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
        DbPathResolver.RequireAbsoluteStorePath(resolved);

        var stopwatch = Stopwatch.StartNew();
        using var db = new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);

        // Same fail-closed schema rule as every writer (rule 14 / finding A).
        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"The store has {pending.Count} pending migration(s) ({string.Join(", ", pending)}) — " +
                $"run pwsh tools/migrate.ps1 -Arena {arena.Id} first (snapshot-first, rule 14).");
        }

        // A WRITING verb dispatches before the Generic Host, so no hosted guard has run (D59/D72).
        SoleWriterGate.Guard(
            db,
            configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions(),
            _logger,
            "signal-backfill");

        // ---- the D108 pin guard: refuse BEFORE any grade exists ----
        RequirePinnedThresholds(db);

        var signalOptions = configuration.GetSection(SignalLibraryOptions.SectionName).Get<SignalLibraryOptions>()
                            ?? new SignalLibraryOptions();
        var horizons = signalOptions.ResolvedHorizonsDays;   // finding 301: never read the raw property
        if (horizons.Count == 0)
        {
            // Unreachable by construction: ResolvedHorizonsDays falls back to the non-empty
            // DefaultHorizonsDays constant, so an empty or absent SignalLibrary:HorizonsDays now means
            // "use [21, 63]" (finding 301), NOT "refuse". Kept as a belt — if it ever fires it is a
            // defect in the default itself, not an operator misconfiguration, and the message must not
            // send them looking at appsettings for a key that is doing what it should.
            throw new InvalidOperationException(
                "SignalLibrary horizons resolved EMPTY — SignalLibraryOptions.DefaultHorizonsDays is " +
                "itself empty (a code defect, not an unconfigured key); nothing to grade (fail closed).");
        }

        // The registry is frozen; registering is idempotent and leaves existing rows untouched.
        var registered = new SignalRegistrar(db).RegisterV1(
            DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (registered > 0) _logger.LogInformation("signal-backfill: registered {Count} signal(s).", registered);

        var calendar = new CalendarService(db);
        var sessions = calendar.SessionsBetween(ParseDate(request.From), ParseDate(request.To)).ToList();
        if (sessions.Count == 0)
        {
            throw new InvalidOperationException(
                $"No trading sessions in [{request.From}, {request.To}] — the calendar is unseeded for the " +
                "window, or the dates are outside the seeded range (fail closed).");
        }

        // The FULL calendar is what resolves t+k: a grade near the end of the requested window needs
        // sessions BEYOND it, and truncating to the request would silently drop those horizons.
        var allSessions = calendar.SessionsBetween(sessions[0], sessions[^1].AddDays(400)).ToList();

        var costs = configuration.GetSection(CostsOptions.SectionName).Get<CostsOptions>() ?? new CostsOptions();
        var watermark = ResolveWatermark(db);
        var proxy = ResolveMarketProxy(db, watermark);

        // Membership resolves through the EXCLUSION-scoped read the replay used (D97) — never the
        // forward slice-scoped one, which would grade the sp100 launch slice rather than the history
        // the library claims to describe.
        var universe = configuration.GetSection(UniverseOptions.SectionName).Get<UniverseOptions>() ?? new UniverseOptions();
        var membership = new ExclusionScopedMembershipRead(new IndexMembershipReadService(db), db, universe);

        var barReads = new BarReadService(db);
        var engine = new SignalIcEngine(
            db, membership, asOf => new BarFeatureView(barReads, calendar, asOf, watermark, costs));

        _logger.LogInformation(
            "signal-backfill: {Count} session(s) {From}..{To} at watermark {Watermark}; horizons [{Horizons}].",
            sessions.Count, sessions[0], sessions[^1], watermark, string.Join(", ", horizons));

        var expectedPerDay = SignalRegistry.V1.Count * horizons.Count;
        var graded = 0;
        var written = 0;
        var alreadyPresent = 0;
        var skipped = 0;
        foreach (var day in sessions)
        {
            ct.ThrowIfCancellationRequested();

            // COVERAGE CHECK BEFORE SCORING (finding 300). A day whose full (signal × horizon) set is
            // already stored is skipped WITHOUT scoring it. Grading it and discarding the rows at
            // persist time produced the right table at the wrong cost — which made "resumable" true
            // only on paper: a crashed multi-hour run resumed at the price of starting over.
            //
            // The check is deliberately for the FULL set, not "any row": a day partially graded when a
            // run was interrupted mid-day, or graded before a signal was registered, must be revisited
            // so the missing pairs land. Skipping on "any row present" would silently freeze those gaps
            // in place, and no re-run would ever fill them.
            var already = engine.GradedOn(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (already.Count >= expectedPerDay)
            {
                skipped++;
                alreadyPresent += already.Count;
                continue;
            }

            var grades = engine.GradeDay(day, SignalRegistry.V1, horizons, allSessions, proxy);
            if (grades.Count == 0) continue;

            graded++;
            var w = engine.Persist(grades);
            written += w;
            alreadyPresent += grades.Count - w;

            // The near-O(N^2) DetectChanges cost the historical backfill already documented: a 5,000-day
            // x 7-signal loop hits it hard without this.
            db.ChangeTracker.Clear();
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "signal-backfill complete: {Graded}/{Planned} session(s) graded, {SkippedDays} already-complete " +
            "session(s) skipped WITHOUT scoring, {Written} row(s) written, {Present} row(s) already present. " +
            "Wall time {Elapsed} (MEASURED on this machine — a measurement, not a promise).",
            graded, sessions.Count, skipped, written, alreadyPresent, stopwatch.Elapsed);

        return await Task.FromResult(
            new SignalBackfillOutcome(sessions.Count, graded, written, alreadyPresent, stopwatch.Elapsed, skipped))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The D108 refusal. Reads the CURRENT config rows (an operational read outside any run's
    /// provenance — the `DetectabilityGate.ResolveCurrent` precedent) and throws with every missing key
    /// named, so the operator is told what to pin rather than left to guess.
    /// </summary>
    private void RequirePinnedThresholds(AlphaLabDbContext db)
    {
        var config = new ConfigReadService(db);
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(config.ResolveCurrent(GoneAlphaKey))) missing.Add(GoneAlphaKey);
        if (string.IsNullOrWhiteSpace(config.ResolveCurrent(DecayAlphaKey))) missing.Add(DecayAlphaKey);
        if (missing.Count == 0) return;

        _logger.LogCritical(
            "signal-backfill: REFUSING to grade — the trend thresholds are not pinned ({Missing}). D108 requires " +
            "them pinned at checkpoint 4.5.2 before the first grade row exists; pinning them afterwards would be " +
            "choosing thresholds by looking at the answer.",
            string.Join(", ", missing));
        throw new SignalThresholdsNotPinnedException(missing);
    }

    /// <summary>MAX(observed_at) over the versioned input tables — the same rule the replay freezes.</summary>
    private static string ResolveWatermark(AlphaLabDbContext db)
    {
        var barMax = db.Bars.Max(b => (string?)b.ObservedAt);
        var caMax = db.CorporateActions.Max(c => (string?)c.ObservedAt);
        var max = string.CompareOrdinal(barMax, caMax) >= 0 ? barMax : caMax ?? barMax;
        return max ?? throw new InvalidOperationException(
            "The store has no bars — nothing to grade. Run the D70 historical backfill first (fail closed).");
    }

    /// <summary>The market proxy `resmom`/`bab` regress against, as-of the watermark (D96). Null is
    /// legitimate: those two scorers then emit nothing rather than degrading (rule 10).</summary>
    private SecurityId? ResolveMarketProxy(AlphaLabDbContext db, string watermark)
    {
        var id = new ConfigReadService(db).ResolveLongAsOf(RegimeProxyIngestion.ProxyConfigKey, watermark);
        if (id is null)
        {
            _logger.LogWarning(
                "signal-backfill: no market proxy resolved ({Key}) — resmom:L252 and bab:L252 will emit no scores.",
                RegimeProxyIngestion.ProxyConfigKey);
        }
        return id is { } v ? new SecurityId(v) : null;
    }

    private static DateOnly ParseDate(string iso) =>
        DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
