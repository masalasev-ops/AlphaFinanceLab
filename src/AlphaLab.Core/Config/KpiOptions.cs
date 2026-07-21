namespace AlphaLab.Core.Config;

/// <summary>
/// The D88 cohort maturation curve (CONFIG_REFERENCE "Kpi"; read-model, descriptive only — never a
/// gate/monitor/allocator input). Every default here MIRRORS that file — it is the single source of
/// truth; never hard-code a value that belongs there.
///
/// Follows the …Options convention (SectionName + mutable get/set defaults matching CONFIG). The
/// CONSUMING phase owns the bind (finding F): registered in AlphaLab.Api where the cohort read-model
/// builder (checkpoints 3.12/3.13) reads it; unbound until then.
/// </summary>
public sealed class KpiOptions
{
    public const string SectionName = "Kpi";

    /// <summary>Admission-vintage bucket width (months) over strategies.created_on — default half-year cohorts.</summary>
    public int CohortBucketMonths { get; set; } = 6;

    /// <summary>Below this live-member count at track length t, the cohort segment renders dimmed
    /// (display='dimmed', reason='thin_cohort').</summary>
    public int CohortMinStrategies { get; set; } = 3;
}
