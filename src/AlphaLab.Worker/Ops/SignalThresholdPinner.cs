using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>What a threshold pin was asked to write (checkpoint 4.5.2, D108).</summary>
/// <param name="Power">
/// The detection power the finding-305 detectability floors are quoted at. OPTIONAL, and the asymmetry
/// with the two α values is deliberate: the α values gate a VERDICT and the backfill refuses to grade
/// without them, whereas power governs only a published DIAGNOSTIC. Omitting it withholds the floors
/// with a stated reason; it never blocks a run and never changes a flag.
/// </param>
public sealed record SignalPinRequest(double GoneAlpha, double DecayAlpha, double? Power = null);

/// <summary>What one pin attempt did. <paramref name="AlreadyPinned"/> lists keys left untouched.</summary>
public sealed record SignalPinOutcome(IReadOnlyList<string> Written, IReadOnlyList<string> AlreadyPinned);

/// <summary>
/// Writes the two D108 trend-flag significance levels as versioned <c>config</c> rows — the ONE
/// sanctioned way to satisfy the FR-45 backfill's pin refusal.
///
/// WHY THIS EXISTS AT ALL. The refusal was built before anything could satisfy it: every other
/// config-row write in the system is a code path that OWNS the value it writes (the D98 calibration
/// freeze, the regime-proxy resolution, the slice snapshot), and rule 15 forbids editing the store by
/// hand. So a guard that demanded two operator-chosen rows had no legitimate way to be satisfied. This
/// verb closes that: the value arrives as an argument, the DERIVATION is recorded in the row's reason,
/// and the write goes through the same append-only path as every other config row.
///
/// PINNED ONCE, NEVER RE-STAMPED (the D98 patience-knob precedent). An already-pinned key is LEFT
/// ALONE and reported, never overwritten — because the whole point of pinning before the first grade
/// row is that the threshold cannot be revised once grades exist to look at. A deliberate later change
/// is a separate, recorded operator act, not a re-run of this verb.
///
/// It refuses a value that is not a significance level (outside 0 &lt; α &lt; 1) rather than clamping:
/// a clamped threshold would be a number nobody chose silently governing a published verdict.
/// </summary>
public sealed class SignalThresholdPinner(
    IConfiguration configuration,
    ArenaOptions arena,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<SignalThresholdPinner>();

    public SignalPinOutcome Run(string connectionString, SignalPinRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        RequireSignificanceLevel(request.GoneAlpha, nameof(request.GoneAlpha));
        RequireSignificanceLevel(request.DecayAlpha, nameof(request.DecayAlpha));
        if (request.Power is { } pw) RequireSignificanceLevel(pw, nameof(request.Power));

        using var arenaScope = _logger.BeginArenaScope(arena);
        var resolved = DbPathResolver.ResolvePath(connectionString, arena.Id);
        DbPathResolver.RequireAbsoluteStorePath(resolved);

        using var db = new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite(resolved).Options);

        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"The store has {pending.Count} pending migration(s) ({string.Join(", ", pending)}) — " +
                $"run pwsh tools/migrate.ps1 -Arena {arena.Id} first (snapshot-first, rule 14).");
        }

        // A writing verb dispatched before the Generic Host carries its own liveness gate (D59/D72).
        SoleWriterGate.Guard(
            db,
            configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions(),
            _logger,
            "signal-pin-thresholds");

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var written = new List<string>();
        var already = new List<string>();

        Pin(db, SignalBackfillRunner.GoneAlphaKey, request.GoneAlpha, stamp,
            "D108 checkpoint 4.5.2: one-sided significance level for the GONE arm (the 5y mean not " +
            "significantly above zero). Derived from the lab's existing standard rather than chosen: " +
            "Gate.Confidence=0.95 and the D56 curve falseAlarmRate=0.05 are the same 5%. The critical " +
            "value is NOT stored — it is t_{1-alpha, df} computed at read time, because df depends on " +
            "the effective sample (n_eff = window/horizon; level arm df = n_eff-1).",
            written, already);

        Pin(db, SignalBackfillRunner.DecayAlphaKey, request.DecayAlpha, stamp,
            "D108 checkpoint 4.5.2: one-sided significance level for the DECAYING arm (the 5y trend " +
            "significantly negative). Derived from the lab's existing standard rather than chosen: " +
            "Gate.Confidence=0.95 and the D56 curve falseAlarmRate=0.05 are the same 5%. Deliberately the " +
            "SAME alpha as the gone arm — the arms differ in what they FIT (a mean vs a slope, hence " +
            "different df) not in how much evidence the lab demands, and a per-arm alpha would be an " +
            "unmotivated free parameter. The critical value is NOT stored — it is t_{1-alpha, df} " +
            "computed at read time, with trend arm df = n_eff-2.",
            written, already);

        if (request.Power is { } power)
        {
            Pin(db, PowerKey, power, stamp,
                "Finding 305, checkpoint 4.5.4: the DETECTION POWER the published minimum-detectable-IC " +
                "is quoted at. Derived from the lab's existing standard rather than chosen: Gate.Power " +
                "= 0.80, the same power D48's MDE uses. The floor itself is NOT stored — it is " +
                "(t_{1-alpha, df} + t_{power, df}) * se, computed at read time, because both the df and " +
                "the standard error depend on the window actually available. The POWER TERM is what " +
                "makes it a detectable EFFECT rather than a restatement of the critical value: without " +
                "it the number would only say what mean would have cleared the bar, not what true edge " +
                "the test could have found. A config ROW rather than an appsettings value for the same " +
                "reason the two alphas are (finding 292: the read-model must resolve it as-of). NOT part " +
                "of the FR-45 pin refusal - it governs a diagnostic, never a verdict.",
                written, already);
        }

        if (written.Count > 0) db.SaveChanges();

        foreach (var key in already)
        {
            _logger.LogWarning(
                "signal-pin-thresholds: {Key} is ALREADY pinned — left untouched. A threshold is pinned once " +
                "and never re-stamped (D108); revising it after grades exist is choosing by looking at the answer.",
                key);
        }
        foreach (var key in written)
        {
            _logger.LogInformation("signal-pin-thresholds: pinned {Key} (version 1, append-only).", key);
        }

        return new SignalPinOutcome(written, already);
    }

    /// <summary>The finding-305 power row. Named here so the pinner and the read-model cannot drift.</summary>
    public const string PowerKey = "SignalLibrary.MinDetectablePower";

    private static void Pin(
        AlphaLabDbContext db, string key, double alpha, string stamp, string reason,
        List<string> written, List<string> already)
    {
        var existing = db.Config.Where(c => c.Key == key).AsEnumerable()
            .OrderByDescending(c => c.Version).FirstOrDefault();
        if (existing is not null) { already.Add(key); return; }

        db.Config.Add(new ConfigRow
        {
            Key = key,
            ValueJson = alpha.ToString("0.####", CultureInfo.InvariantCulture),
            Version = 1,
            ChangedOn = stamp,
            Reason = reason,
        });
        written.Add(key);
    }

    private static void RequireSignificanceLevel(double alpha, string name)
    {
        if (alpha is <= 0 or >= 1 || double.IsNaN(alpha))
        {
            throw new ArgumentOutOfRangeException(name, alpha,
                "A significance level must lie strictly between 0 and 1. Refusing rather than clamping — a " +
                "clamped threshold would be a number nobody chose silently governing a published verdict.");
        }
    }
}
