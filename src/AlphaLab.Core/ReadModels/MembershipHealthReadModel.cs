namespace AlphaLab.Core.ReadModels;

/// <summary>
/// The three states of the membership feed's health (checkpoint 6.4). THREE, not two, and the third is
/// the load-bearing one.
/// </summary>
public enum MembershipHealthState
{
    /// <summary>Fetched recently and the two sources agreed — the roster is verified.</summary>
    FreshAndAgreeing,

    /// <summary>Either the last fetch is older than the staleness budget, or the last reconcile HELD on
    /// a count-sanity breach or a source divergence. Both mean the same thing to an operator: the
    /// roster on disk is not currently backed by two agreeing sources.</summary>
    StaleOrDiverging,

    /// <summary>
    /// No fetch instant is recorded, so freshness cannot be judged at all.
    ///
    /// **Required by the provenance ruling, not a defensive extra.** Every `index_membership_log` row
    /// written before M11 carries `observed_at = NULL`, deliberately un-backfilled — the only value
    /// available was `as_of`, the run date, and writing it would have manufactured provenance that looks
    /// authoritative and is false. A two-state model would render those rows as STALE, and an operator
    /// would open the lab on day one and go chasing a divergence that does not exist. Unprovenanced is
    /// not the same as old.
    /// </summary>
    Unknown,
}

/// <summary>
/// The membership feed's health, as a serializable read-model (D58 / rule 18) — finding 197's third
/// deliverable, and the answer to its actual objection, which is that a divergence was INVISIBLE.
///
/// **This is a VISIBLE STATE, not an alarm that fires, and that is a recorded choice rather than an
/// omission.** Nothing polls this; nothing pages anyone. It is read when a screen asks for it. The
/// producer side already fails closed and logs at Error on a hold, which is what stops a bad roster from
/// being applied; this is what makes the resulting state legible afterwards. If a future reader needs
/// something that ACTIVELY fires, that is new work with its own decision — it does not exist here, and
/// this sentence is here so nobody assumes it does.
///
/// **Freshness and last-validation are separate fields on purpose** (UX-11(a): each Data-health cell
/// carries "freshness, last-validation result, and watermark"). A roster can be freshly fetched AND
/// diverging, or agreeing but weeks old, and collapsing the two would hide exactly the case the operator
/// most needs to see.
///
/// The Phase-7 Data-health grid renders this; building the grid is NOT 6.4's work and the declination is
/// recorded rather than silent (UX_GUIDELINES §UX-11 puts the full surface in Phase 7). The read-model is
/// the durable artefact — with it, the grid becomes a layout exercise instead of a plumbing one.
/// </summary>
public sealed record MembershipHealthReadModel
{
    public required MembershipHealthState State { get; init; }

    /// <summary>
    /// When the roster was actually FETCHED (UTC ISO-8601), or null when no row records one.
    ///
    /// This is `index_membership_log.observed_at`, NEVER `as_of` and never the run date — finding 197
    /// exists because the cell showed the latter. A null here is exactly what makes the state Unknown.
    /// </summary>
    public string? FetchedAt { get; init; }

    /// <summary>The session the last reconcile ran for. Kept BESIDE <see cref="FetchedAt"/> rather than
    /// instead of it, so the difference between "when we ran" and "when the data arrived" is visible
    /// rather than something a reader has to know to ask about.</summary>
    public string? LastReconcileAsOf { get; init; }

    /// <summary>Which primary produced the last row (`oef_csv`, `ivv_csv`, …), or null on a pre-M11 row.
    /// Load-bearing at the rule-22 widen, when both write into this one table.</summary>
    public string? Source { get; init; }

    /// <summary>True when the last reconcile's two sources agreed and the diff was applied; false when it
    /// HELD; null when there is no reconcile to report.</summary>
    public bool? LastValidationAgreed { get; init; }

    /// <summary>
    /// Why the state is what it is, in plain language, ALWAYS populated — including for
    /// <see cref="MembershipHealthState.Unknown"/>, whose whole point is that "no reading" must be
    /// distinguishable from "a bad reading". UX-17's precedent: a missing number renders its reason
    /// inline, never an empty cell.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>The count-sanity/divergence note from a held reconcile — the operator's first lead, and
    /// what RUNBOOK's "inspect index_membership_log diffs" instruction is looking for. Null when the
    /// last reconcile applied.</summary>
    public string? HeldReason { get; init; }

    /// <summary>Nothing has ever been reconciled in this arena — a fresh store, not a fault.</summary>
    public static MembershipHealthReadModel NoReconcileYet { get; } = new()
    {
        State = MembershipHealthState.Unknown,
        Reason = "No membership reconcile has run in this arena yet, so there is nothing to report. "
               + "This is the state of a fresh store, not a fault.",
    };
}
