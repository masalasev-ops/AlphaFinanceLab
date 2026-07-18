using System.Text.Json;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Json;

namespace AlphaLab.Core.Funnel;

/// <summary>One security's score from Stage 2, as a list entry rather than a map key — so the JSON
/// stays an array of records a human can scan, and so ties keep their deterministic order.</summary>
public sealed record ScoredName(SecurityId SecurityId, double Score, int Rank);

/// <summary>Stage 4's plan, flattened for the snapshot.</summary>
public sealed record Stage4Snapshot(
    IReadOnlyList<SecurityId> Opens,
    IReadOnlyList<SecurityId> Holds,
    IReadOnlyList<PlannedClose> Closes,
    RebalanceScope Scope);

/// <summary>
/// The funnel's stage-1..6 snapshot for ONE strategy-day — the thing that becomes
/// `decisions.stage_json` (SCHEMA:204-209, "Why this trade" provenance).
///
/// IT HAS TWO JOBS, and the second is the one that is easy to forget.
///
/// (1) PROVENANCE. A year later, "why did the lab buy X on this date?" must be answerable from the
/// row alone — which names were eligible, what they scored, what got picked, what the exit policy
/// said about the existing book, how it was sized, and what was ordered. Every stage's reasons ride
/// along in <see cref="Notes"/>.
///
/// (2) THE T→T+1 CARRIER. <see cref="Stage6Orders"/> is read back by the NEXT session's run, which
/// fills them at its open. This is why the snapshot must round-trip exactly, and why the orders
/// cannot simply be recomputed tomorrow: tomorrow's run reads at a LATER WATERMARK, so if a
/// correction to today's bars arrives overnight, a recomputed funnel would produce different orders
/// than the lab actually decided — it would rewrite its own history. The decision is a fact about
/// what happened, not a function to re-evaluate.
///
/// SIZE. This is deliberately complete rather than trimmed: every eligible name, every score, every
/// reason. At ~101 names that is a few KB per strategy-day. Truncating it to save disk would make
/// the provenance partial, which is the one thing it cannot be — but it is worth watching at Phase-4
/// replay scale (~5,000 sessions × strategies), and PROGRESS records that.
/// </summary>
public sealed record DecisionSnapshot
{
    /// <summary>Shape version. Stage_json is read back by a LATER run (and by Phase-3 read-models),
    /// so a shape change must be detectable rather than silently mis-parsed.</summary>
    public string SnapshotVersion { get; init; } = CurrentVersion;

    public required string StrategyId { get; init; }

    /// <summary>The decision date T (ISO).</summary>
    public required string AsOf { get; init; }

    /// <summary>The run's data watermark. Recorded because the whole snapshot is only meaningful
    /// relative to it — the same asOf at a different watermark is a different decision.</summary>
    public required string Watermark { get; init; }

    public required IReadOnlyList<SecurityId> Stage1Eligible { get; init; }

    public required IReadOnlyList<ScoredName> Stage2Scores { get; init; }

    public required IReadOnlyList<SecurityId> Stage3WishList { get; init; }

    public required Stage4Snapshot Stage4 { get; init; }

    public required IReadOnlyList<TargetPosition> Stage5Targets { get; init; }

    /// <summary>Cash left unallocated by Stage 5. On a sparse day this is large, and that is the
    /// no-padding invariant showing its work rather than a defect.</summary>
    public required decimal Stage5UninvestedCash { get; init; }

    /// <summary>THE ORDERS. Decided at close <see cref="AsOf"/>, filled at the next session's open.</summary>
    public required IReadOnlyList<PlannedOrder> Stage6Orders { get; init; }

    /// <summary>Every stage's reasons, tagged with the stage that emitted them.</summary>
    public required IReadOnlyList<StageNote> Notes { get; init; }

    public const string CurrentVersion = "ds-1.0";

    public string ToJson() => JsonSerializer.Serialize(this, AlphaLabJson.Options);

    /// <summary>Read a snapshot back (the T+1 fill path, and Phase 3's read-models). Throws on an
    /// unknown <see cref="SnapshotVersion"/> rather than best-effort parsing a shape it does not
    /// understand — a half-understood order list is worse than a stopped run (rule 10).</summary>
    public static DecisionSnapshot FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        // Check the version BEFORE deserializing the whole shape. A future ds-2.0 could rename or
        // retype any field, so a full deserialize would throw an opaque JsonException about some
        // inner property when the real, actionable cause is "this build cannot read that version".
        // Peek the discriminator, fail closed with the clear message, and only then bind the body.
        var declaredVersion = PeekVersion(json);
        if (declaredVersion != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"decisions.stage_json has snapshot_version '{declaredVersion}' but this build reads " +
                $"'{CurrentVersion}'. Refusing to guess at an unknown shape — an order list parsed on a hopeful " +
                "reading is worse than a stopped run (rule 10).");
        }

        return JsonSerializer.Deserialize<DecisionSnapshot>(json, AlphaLabJson.Options)
            ?? throw new InvalidOperationException("decisions.stage_json deserialized to null.");
    }

    private static string PeekVersion(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("snapshot_version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException(
                "decisions.stage_json has no string 'snapshot_version' — it is not a snapshot this build wrote, " +
                "and its shape cannot be trusted (rule 10).");
    }
}

/// <summary>A funnel note tagged with the stage that emitted it, so a reader knows whether a name
/// was dropped for having no bar (Stage 1), no score (Stage 3), or no price (Stage 6).</summary>
public sealed record StageNote(int Stage, SecurityId SecurityId, string Reason)
{
    public static IEnumerable<StageNote> From(int stage, IEnumerable<FunnelNote> notes) =>
        notes.Select(n => new StageNote(stage, n.Id, n.Reason));
}
