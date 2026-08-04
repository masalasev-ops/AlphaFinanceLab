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

    /// <summary>Pin the two D110 proposal-score parameters as versioned config rows, BEFORE the first
    /// proposal exists (checkpoint 5.7). Same shape and same reason as SignalPinThresholds: a parameter
    /// chosen after the scores are visible is a parameter chosen by looking at the answer.</summary>
    PinProposalThresholds,

    /// <summary>The D106/D117 recompute harness (MASTER §25): score a monitor- or gate-rule change by
    /// re-deriving verdicts from the stored generation instead of paying a multi-day replay. REPORT-ONLY —
    /// it writes an artefact under docs/calibration and never a row (D117 clause 1).</summary>
    ReplayRecompute,

    /// <summary>The D120 stored-corpus quality sweep (findings 350/351): re-run the CURRENT data-quality
    /// gate + the member-window detectors over the stored bar corpus (which predates the v1.9.41 R2
    /// guard) and report the securities recommended for `Universe:Exclusions`. REPORT-ONLY — it writes
    /// an artefact under docs/calibration and never a row, a flag, or a config value.</summary>
    StoreSweep,

    /// <summary>The D123/FR-47 construction study (Phase 5.5): measure each registered signal's tracking
    /// error — and therefore its detectability floor — under a LONG-ONLY and a LONG-SHORT construction,
    /// to decide whether this arena can adjudicate a realistic edge at all. REPORT-ONLY — it writes an
    /// artefact under docs/calibration and never a row, a flag, or a config value.</summary>
    ConstructionStudy,
}

/// <summary>The `replay-recompute` request: the candidate rule change, and whether this run is the §25.3
/// parity check (an empty spec against generation 1's own records).</summary>
public sealed record RecomputeRequest(IReadOnlyDictionary<string, string> Overrides, bool VerifyParity, string? SpecName);

/// <summary>The parsed command. <see cref="Date"/> is set only for
/// <see cref="WorkerCommandKind.ReproduceDay"/>; <see cref="Replay"/>/<see cref="ReportOnly"/> only for
/// <see cref="WorkerCommandKind.ReplayCalibrate"/>.</summary>
public sealed record WorkerCommand(
    WorkerCommandKind Kind, string? Date = null, string? ArenaId = null, ReplayRequest? Replay = null,
    bool ReportOnly = false, SignalBackfillRequest? SignalBackfill = null,
    SignalPinRequest? SignalPin = null, ProposalPinRequest? ProposalPin = null,
    RecomputeRequest? Recompute = null, ConstructionStudyRequest? ConstructionStudy = null);

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
    public const string PinProposalThresholdsVerb = "pin-proposal-thresholds";
    public const string ReplayRecomputeVerb = "replay-recompute";
    public const string StoreSweepVerb = "store-sweep";
    public const string ConstructionStudyVerb = "construction-study";

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

        if (string.Equals(verb, StoreSweepVerb, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkerCommand(WorkerCommandKind.StoreSweep, null, arena);
        }

        if (string.Equals(verb, ConstructionStudyVerb, StringComparison.OrdinalIgnoreCase))
        {
            var from = RequireDate(ConstructionStudyVerb, "--from", ValueOf(args, "--from"));
            var to = RequireDate(ConstructionStudyVerb, "--to", ValueOf(args, "--to"));
            if (string.CompareOrdinal(from, to) >= 0)
            {
                throw new ArgumentException($"{ConstructionStudyVerb}: --from ({from}) must precede --to ({to}).");
            }

            // --tail-fraction is OPTIONAL and defaults to deciles. Present-but-unparseable is still
            // refused, so a typo cannot silently become "the default" and produce a report whose header
            // disagrees with what was actually measured.
            var tailRaw = ValueOf(args, "--tail-fraction");
            double? tail = null;
            if (tailRaw is not null)
            {
                if (!double.TryParse(tailRaw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t) || t is <= 0 or > 0.5)
                {
                    throw new ArgumentException(
                        $"{ConstructionStudyVerb}: --tail-fraction must be in (0, 0.5] (got '{tailRaw}'). " +
                        "Above 0.5 the two tails would overlap and they would not be two portfolios.");
                }
                tail = t;
            }

            // --borrow-bp is repeatable: each value is one stated ASSUMPTION about stock-borrow cost,
            // applied to the short leg only. Omitted means the default pair (0 and 40 bp/yr) — the
            // optimistic bound plus a general-collateral figure.
            var borrows = new List<double>();
            for (var i = 1; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "--borrow-bp", StringComparison.OrdinalIgnoreCase)) continue;
                if (!double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var bp) || bp < 0)
                {
                    throw new ArgumentException(
                        $"{ConstructionStudyVerb}: --borrow-bp expects a non-negative number of basis " +
                        $"points per year, got '{args[i + 1]}'.");
                }
                borrows.Add(bp);
            }

            return new WorkerCommand(WorkerCommandKind.ConstructionStudy, null, arena,
                ConstructionStudy: new ConstructionStudyRequest(
                    from, to, tail, borrows.Count > 0 ? borrows : null));
        }

        if (string.Equals(verb, ReplayRecomputeVerb, StringComparison.OrdinalIgnoreCase))
        {
            // --set key=value, repeatable. A bare `replay-recompute` is the §25.3 PARITY run: no overrides,
            // generation 1's own rules, compared against generation 1's own records.
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "--set", StringComparison.OrdinalIgnoreCase)) continue;
                var pair = args[i + 1];
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0 || eq == pair.Length - 1)
                {
                    throw new ArgumentException(
                        $"{ReplayRecomputeVerb}: --set expects key=value, got '{pair}'.");
                }
                overrides[pair[..eq]] = pair[(eq + 1)..];
            }
            var verifyParity = args.Contains("--verify-parity") || overrides.Count == 0;
            return new WorkerCommand(WorkerCommandKind.ReplayRecompute, null, arena,
                Recompute: new RecomputeRequest(overrides, verifyParity, ValueOf(args, "--name")));
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
            // --power is OPTIONAL, unlike the two alphas: it governs the finding-305 detectability
            // floors, which are a diagnostic and never a verdict input. Present-but-unparseable is still
            // refused, so a typo cannot silently become "omitted".
            var powerRaw = ValueOf(args, "--power");
            double? power = powerRaw is null
                ? null
                : RequireAlpha(SignalPinThresholdsVerb, "--power", powerRaw);
            return new WorkerCommand(WorkerCommandKind.SignalPinThresholds, null, arena,
                SignalPin: new SignalPinRequest(gone, decay, power));
        }

        if (string.Equals(verb, PinProposalThresholdsVerb, StringComparison.OrdinalIgnoreCase))
        {
            // BOTH required and explicit, unlike signal-pin-thresholds' optional --power: each of these
            // governs a published SCORE rather than a diagnostic, and a missing value silently defaulting
            // would record a decision nobody made.
            var clampRaw = ValueOf(args, "--prior-clamp");
            if (clampRaw is null ||
                !double.TryParse(clampRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var clamp))
            {
                throw new ArgumentException(
                    $"{PinProposalThresholdsVerb} requires --prior-clamp <0..0.5> (got '{clampRaw ?? "(none)"}').");
            }
            if (clamp is <= 0 or >= 0.5)
            {
                throw new ArgumentException(
                    $"{PinProposalThresholdsVerb}: --prior-clamp must be strictly between 0 and 0.5 (got {clampRaw}).");
            }

            var minRaw = ValueOf(args, "--min-closed");
            if (minRaw is null ||
                !int.TryParse(minRaw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var minClosed) || minClosed < 1)
            {
                throw new ArgumentException(
                    $"{PinProposalThresholdsVerb} requires --min-closed <int >= 1> (got '{minRaw ?? "(none)"}').");
            }

            return new WorkerCommand(WorkerCommandKind.PinProposalThresholds, null, arena,
                ProposalPin: new ProposalPinRequest(clamp, minClosed));
        }

        throw new ArgumentException(
            $"Unknown command '{verb}'. Expected '{ReproduceDayVerb}', '{VerifyWalVerb}', " +
            $"'{ReplayCalibrateVerb}', '{SignalBackfillVerb}', '{SignalPinThresholdsVerb}', " +
            $"'{PinProposalThresholdsVerb}', '{ReplayRecomputeVerb}', '{StoreSweepVerb}', " +
            $"'{ConstructionStudyVerb}', or no verb at " +
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
