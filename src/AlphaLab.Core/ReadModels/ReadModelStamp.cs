namespace AlphaLab.Core.ReadModels;

/// <summary>
/// Discriminant for <see cref="ReadModelStamp"/> (D66). Serializes as a snake_case
/// string ("no_run_yet" / "stamped") via the shared JSON policy.
/// </summary>
public enum ReadModelStampStatus
{
    /// <summary>No run has ever been committed to the system (Phase 0's universal case).</summary>
    NoRunYet,

    /// <summary>A run context is present. run_id/watermark/as_of are non-null.</summary>
    Stamped,
}

/// <summary>
/// The read-model stamp is a discriminated union, not a nullable flat stamp (D66).
/// It is ALWAYS a present object carrying <see cref="Status"/>; <see cref="RunId"/>,
/// <see cref="Watermark"/> and <see cref="AsOf"/> are non-null iff Status == Stamped.
/// The discriminant is about run-context presence only — a strategy that exists but has
/// zero trades is still "stamped". Replay quarantine is orthogonal (a replay read-model is
/// "stamped" with quarantined:true, never a third status).
/// Serialized keys are literally status / run_id / watermark / as_of, and null values are
/// emitted verbatim (the JSON policy must NOT drop nulls — see AlphaLabJson).
/// </summary>
public sealed record ReadModelStamp
{
    public required ReadModelStampStatus Status { get; init; }

    /// <summary>runs.run_id this read-model was projected from; null iff no_run_yet.</summary>
    public long? RunId { get; init; }

    /// <summary>Max observed_at visible (D40); UTC ISO-8601; null iff no_run_yet.</summary>
    public string? Watermark { get; init; }

    /// <summary>The trading day processed; ISO date; null iff no_run_yet.</summary>
    public string? AsOf { get; init; }

    /// <summary>The canonical empty stamp — { status: "no_run_yet", run_id: null, watermark: null, as_of: null }.</summary>
    public static ReadModelStamp NoRunYet { get; } = new() { Status = ReadModelStampStatus.NoRunYet };

    /// <summary>Build a stamped stamp from a committed run's context.</summary>
    public static ReadModelStamp Stamped(long runId, string watermark, string asOf) => new()
    {
        Status = ReadModelStampStatus.Stamped,
        RunId = runId,
        Watermark = watermark,
        AsOf = asOf,
    };
}
