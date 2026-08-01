using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.Candidates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlphaLab.Worker.Ops;

/// <summary>What a proposal-threshold pin was asked to write (checkpoint 5.7, D110).</summary>
/// <param name="PriorClamp">The clamp applied to a stated prior before the log rule sees it, in (0, 0.5).</param>
/// <param name="MinClosed">Minimum CLOSED proposals before any calibration figure is published, ≥ 1.</param>
public sealed record ProposalPinRequest(double PriorClamp, int MinClosed);

/// <summary>
/// Writes the two D110 proposal-score parameters as versioned <c>config</c> rows — the ONE sanctioned way
/// to satisfy the hypotheses endpoint's pin refusal.
///
/// **The `signal-pin-thresholds` model, for the same reason it exists.** A guard demanding operator-chosen
/// rows had no legitimate way to be satisfied: every other config-row write is a code path that OWNS the
/// value it writes, and rule 15 forbids editing the store by hand. The value arrives as an argument, its
/// DERIVATION is recorded in the row's `reason`, and the write goes through the same append-only path as
/// every other config row.
///
/// **PINNED ONCE, NEVER RE-STAMPED** (the D98/D108 precedent). An already-pinned key is left alone and
/// reported. The whole point of pinning before the first proposal is that the parameter cannot be revised
/// once scores exist to look at — and D110's R3 is explicit that the response to a flat improvement trend
/// is to change the researcher's INPUTS, never the measurement, the clamp or the thresholds.
///
/// **Both values are REQUIRED and explicit.** A missing value silently defaulting is exactly what defeats
/// pin-before-use: the operator would not have chosen anything, and the row would record a decision
/// nobody made.
/// </summary>
public sealed class ProposalThresholdPinner(
    IConfiguration configuration,
    ArenaOptions arena,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ProposalThresholdPinner>();

    public SignalPinOutcome Run(string connectionString, ProposalPinRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        RequireClamp(request.PriorClamp);
        RequireMinClosed(request.MinClosed);

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
            "pin-proposal-thresholds");

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var written = new List<string>();
        var already = new List<string>();

        Pin(db, ProposalScoreKeys.PriorClamp,
            request.PriorClamp.ToString("0.####", CultureInfo.InvariantCulture), stamp,
            "D110 checkpoint 5.7: the clamp applied to a stated prior before the LOG scoring rule sees " +
            "it. A log rule is unbounded at 0 and 1, so an unclamped prior of exactly 1.0 on a refuted " +
            "claim scores minus infinity and destroys every aggregate it enters. The clamp bounds the " +
            "PENALTY; it is not a correction to the researcher, and the endpoint separately refuses a " +
            "prior outside (0,1) rather than clamping it into range - a clamped INPUT would be a number " +
            "nobody stated being scored as though they had. Pinned before the first proposal exists " +
            "because a parameter chosen after scores are visible is a parameter chosen by looking at " +
            "the answer (D110 R3).",
            written, already);

        Pin(db, ProposalScoreKeys.ScoreMinClosed,
            request.MinClosed.ToString(CultureInfo.InvariantCulture), stamp,
            "D110 checkpoint 5.7: the minimum number of CLOSED proposals before a calibration-skill " +
            "figure is published at all. The leave-one-out base rate b is estimated from the closed set, " +
            "so below this count the REFERENCE POINT is noise and the skill measured against it is noise " +
            "about noise. Publishing an 'insufficient' verdict is the honest output; publishing a number " +
            "is not - the same discipline as the effective-sample rail beside a trend flag (finding 290).",
            written, already);

        if (written.Count > 0) db.SaveChanges();

        foreach (var key in already)
        {
            _logger.LogWarning(
                "pin-proposal-thresholds: {Key} is ALREADY pinned — left untouched. A score parameter is " +
                "pinned once and never re-stamped (D110 R3); revising it after proposals exist is choosing " +
                "by looking at the answer.",
                key);
        }
        foreach (var key in written)
        {
            _logger.LogInformation("pin-proposal-thresholds: pinned {Key} (version 1, append-only).", key);
        }

        return new SignalPinOutcome(written, already);
    }

    private static void Pin(
        AlphaLabDbContext db, string key, string valueJson, string stamp, string reason,
        List<string> written, List<string> already)
    {
        var existing = db.Config.Where(c => c.Key == key).AsEnumerable()
            .OrderByDescending(c => c.Version).FirstOrDefault();
        if (existing is not null) { already.Add(key); return; }

        db.Config.Add(new ConfigRow
        {
            Key = key,
            ValueJson = valueJson,
            Version = 1,
            ChangedOn = stamp,
            Reason = reason,
        });
        written.Add(key);
    }

    /// <summary>The clamp is a probability MARGIN, so it lives in (0, 0.5): at 0.5 it would collapse every
    /// prior to the same value and the channel would measure nothing.</summary>
    private static void RequireClamp(double clamp)
    {
        if (clamp is <= 0 or >= 0.5 || double.IsNaN(clamp))
        {
            throw new ArgumentOutOfRangeException(nameof(clamp), clamp,
                "The prior clamp must lie strictly between 0 and 0.5. Refusing rather than clamping the " +
                "clamp — at 0.5 every prior collapses to the same value and the calibration channel " +
                "measures nothing, which is a silent failure rather than a loud one.");
        }
    }

    private static void RequireMinClosed(int minClosed)
    {
        if (minClosed < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minClosed), minClosed,
                "The minimum closed-proposal count must be at least 1 — a base rate estimated from zero " +
                "closed outcomes is not an estimate.");
        }
    }
}
