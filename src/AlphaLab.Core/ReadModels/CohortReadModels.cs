namespace AlphaLab.Core.ReadModels;

// The D88/FR-39 cohort maturation read-model (UX-15). Descriptive ONLY — never a gate, monitor, or
// allocator input. Answers "is the researcher loop learning what to test, or just recombining?" by
// comparing later admission cohorts to earlier ones at EQUAL maturity (age-aligned, never wall-clock).
// The rails against a flattering picture are resolved into the data itself: retired members stay in their
// cohort (no survivorship); thin and sub-MDE segments ship dimmed; replay cohorts are quarantined and
// never co-plotted with forward cohorts.

/// <summary>One age-aligned point on a cohort's maturation curve: at track length <see cref="T"/> (trading
/// days since admission), the cohort's median D36 population percentile (the S3 source, reused verbatim)
/// with its 25–75% band. <see cref="Display"/>/<see cref="Reason"/> carry the honesty dimming.</summary>
public sealed record CohortPoint(
    int T, int MemberCountAtT, double MedianPercentile, double BandLo, double BandHi, string Display, string? Reason)
{
    public const string DisplayNormal = "normal";
    public const string DisplayDimmed = "dimmed";
    public const string ReasonThinCohort = "thin_cohort";
    public const string ReasonInsideMde = "inside_mde";
}

/// <summary>An admission-vintage cohort (strategies.created_on bucketed by Kpi.CohortBucketMonths). Replay
/// cohorts carry <see cref="Quarantined"/>=true and MUST render in a separate strip (never co-plotted).</summary>
public sealed record Cohort(string Label, int MemberCount, bool Quarantined, IReadOnlyList<CohortPoint> Series);

/// <summary>The cohort maturation curve read-model (FR-39). Forward and quarantined-replay cohorts share
/// one list, distinguished by <see cref="Cohort.Quarantined"/> — the client renders them in separate strips.</summary>
public sealed record CohortMaturationReadModel
{
    public required ReadModelStamp Stamp { get; init; }
    public IReadOnlyList<Cohort> Cohorts { get; init; } = [];
    public static CohortMaturationReadModel NoRunYet { get; } = new() { Stamp = ReadModelStamp.NoRunYet };
}
