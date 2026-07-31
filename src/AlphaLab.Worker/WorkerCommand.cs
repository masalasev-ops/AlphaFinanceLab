using AlphaLab.Worker.Ops;

namespace AlphaLab.Worker;

/// <summary>What a Worker launch was asked to do.</summary>
public enum WorkerCommandKind
{
    /// <summary>The default: the D61/D72 daily launch (catch up, drain, back up, exit) or, with
    /// --serve, the resident Scheduled host.</summary>
    Daily,

    /// <summary>Re-run one committed past session from its stored watermark into a scratch store and
    /// compare, byte for byte, against what was committed (FR-25 / NFR-1). Read-only against the arena.</summary>
    ReproduceDay,

    /// <summary>Assert journal_mode=WAL is active on the arena store and that a checkpoint completes
    /// (FR-25). Read-mostly: it never SETS the pragma.</summary>
    VerifyWal,

    /// <summary>The Phase-4 Arena Replay + calibration chain (FR-19/36, D95): replay the window under
    /// run_kind='replay' at the frozen watermark; the curve build + report + config freeze steps attach
    /// per checkpoints 4.6–4.8. WRITES to the arena (quarantined rows) — the Worker is the sole writer.</summary>
    ReplayCalibrate,

    /// <summary>The Phase-4.5 signal-IC backfill (FR-45, D91): grade every registered signal over the
    /// stored history. WRITES `signal_ic` — hence a Worker verb, not a tools/Backfill mode (D59). It is
    /// NOT a replay generation (D95) and refuses to run until the D108 thresholds are pinned.</summary>
    SignalBackfill,

    /// <summary>Pin the two D108 trend-flag significance levels as versioned config rows (checkpoint
    /// 4.5.2) — the one sanctioned way to satisfy the backfill's pin refusal, since rule 15 forbids
    /// editing the store by hand. Pinned once; an existing key is left untouched.</summary>
    SignalPinThresholds,
}

/// <summary>The parsed command. <see cref="Date"/> is set only for
/// <see cref="WorkerCommandKind.ReproduceDay"/>; <see cref="Replay"/>/<see cref="ReportOnly"/> only for
/// <see cref="WorkerCommandKind.ReplayCalibrate"/>.</summary>
public sealed record WorkerCommand(
    WorkerCommandKind Kind, string? Date = null, string? ArenaId = null, ReplayRequest? Replay = null,
    bool ReportOnly = false, SignalBackfillRequest? SignalBackfill = null,
    SignalPinRequest? SignalPin = null);

/// <summary>
/// Pure parsing of the Worker's command line (the <see cref="WorkerModeParser"/> precedent —
/// side-effect-free, so the interesting cases are unit-testable without a host).
///
/// <code>
///   dotnet run --project src/AlphaLab.Worker                                  -> Daily (OnDemand)
///   dotnet run --project src/AlphaLab.Worker -- --serve                       -> Daily (Scheduled)
///   dotnet run --project src/AlphaLab.Worker -- reproduce-day --date 2026-07-22 [--arena sp500]
///   dotnet run --project src/AlphaLab.Worker -- verify-wal [--arena sp500]
/// </code>
///
/// The verb is positional and must lead, so it can never be confused with a value. An unknown verb
/// FAILS rather than silently falling through to the daily run: a mistyped `reproduce-day` that
/// quietly launched the sole writer against the live arena would be a genuinely bad surprise
/// (rule 10).
/// </summary>
public static class WorkerCommandParser
{
    public const string ReproduceDayVerb = "reproduce-day";
    public const string VerifyWalVerb = "verify-wal";
    public const string ReplayCalibrateVerb = "replay-calibrate";
    public const string SignalBackfillVerb = "signal-backfill";
    public const string SignalPinThresholdsVerb = "signal-pin-thresholds";

    public static WorkerCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0].StartsWith('-')) return new WorkerCommand(WorkerCommandKind.Daily);

        var verb = args[0];
        var arena = ValueOf(args, "--arena");

        if (string.Equals(verb, ReproduceDayVerb, StringComparison.OrdinalIgnoreCase))
        {
            var date = ValueOf(args, "--date")
                ?? throw new ArgumentException(
                    $"{ReproduceDayVerb} requires --date <yyyy-MM-dd>: the session to reproduce.");
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
            {
                throw new ArgumentException($"{ReproduceDayVerb}: --date '{date}' is not a yyyy-MM-dd date.");
            }
            return new WorkerCommand(WorkerCommandKind.ReproduceDay, date, arena);
        }

        if (string.Equals(verb, VerifyWalVerb, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkerCommand(WorkerCommandKind.VerifyWal, null, arena);
        }

        if (string.Equals(verb, ReplayCalibrateVerb, StringComparison.OrdinalIgnoreCase))
        {
            var from = RequireDate(ReplayCalibrateVerb, "--from", ValueOf(args, "--from"));
            var to = RequireDate(ReplayCalibrateVerb, "--to", ValueOf(args, "--to"));
            if (string.CompareOrdinal(from, to) >= 0)
            {
                throw new ArgumentException($"{ReplayCalibrateVerb}: --from ({from}) must precede --to ({to}).");
            }
            var learnThrough = ValueOf(args, "--learn-through");
            if (learnThrough is not null) learnThrough = RequireDate(ReplayCalibrateVerb, "--learn-through", learnThrough);
            return new WorkerCommand(WorkerCommandKind.ReplayCalibrate, null, arena,
                new ReplayRequest(from, to, ValueOf(args, "--watermark"), learnThrough, args.Contains("--reset")),
                ReportOnly: args.Contains("--report-only"));
        }

        if (string.Equals(verb, SignalBackfillVerb, StringComparison.OrdinalIgnoreCase))
        {
            var from = RequireDate(SignalBackfillVerb, "--from", ValueOf(args, "--from"));
            var to = RequireDate(SignalBackfillVerb, "--to", ValueOf(args, "--to"));
            if (string.CompareOrdinal(from, to) >= 0)
            {
                throw new ArgumentException($"{SignalBackfillVerb}: --from ({from}) must precede --to ({to}).");
            }
            return new WorkerCommand(WorkerCommandKind.SignalBackfill, null, arena,
                SignalBackfill: new SignalBackfillRequest(from, to));
        }

        if (string.Equals(verb, SignalPinThresholdsVerb, StringComparison.OrdinalIgnoreCase))
        {
            var gone = RequireAlpha(SignalPinThresholdsVerb, "--gone-alpha", ValueOf(args, "--gone-alpha"));
            var decay = RequireAlpha(SignalPinThresholdsVerb, "--decay-alpha", ValueOf(args, "--decay-alpha"));
            return new WorkerCommand(WorkerCommandKind.SignalPinThresholds, null, arena,
                SignalPin: new SignalPinRequest(gone, decay));
        }

        throw new ArgumentException(
            $"Unknown command '{verb}'. Expected '{ReproduceDayVerb}', '{VerifyWalVerb}', " +
            $"'{ReplayCalibrateVerb}', '{SignalBackfillVerb}', '{SignalPinThresholdsVerb}', or no verb at " +
            "all (the daily launch). Refusing to fall through to the daily run on a typo — that would start " +
            "the sole DB writer against the live arena.");
    }

    /// <summary>A significance level must be present and strictly inside (0,1). Both halves are explicit
    /// rather than defaulted: a MISSING alpha silently defaulting to 0.05 would defeat the whole
    /// pin-before-grade discipline, which requires the operator to state the value deliberately.</summary>
    private static double RequireAlpha(string verb, string flag, string? value)
    {
        if (value is null ||
            !double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var alpha))
        {
            throw new ArgumentException($"{verb} requires {flag} <0..1> (got '{value ?? "(none)"}').");
        }
        if (alpha is <= 0 or >= 1)
        {
            throw new ArgumentException($"{verb}: {flag} must be strictly between 0 and 1 (got {value}).");
        }
        return alpha;
    }

    private static string RequireDate(string verb, string flag, string? value)
    {
        // InvariantCulture: the provider-less overload validates against the OS calendar — on ar-SA
        // (Umm al-Qura) a perfectly valid ISO year is out of range and the CLI would reject it.
        if (value is null || !DateOnly.TryParseExact(value, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
        {
            throw new ArgumentException($"{verb} requires {flag} <yyyy-MM-dd> (got '{value ?? "(none)"}').");
        }
        return value;
    }

    private static string? ValueOf(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }
}
