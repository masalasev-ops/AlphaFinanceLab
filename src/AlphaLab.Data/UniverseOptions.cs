namespace AlphaLab.Data;

/// <summary>
/// Universe / membership configuration (CONFIG_REFERENCE "Universe"). Bound from the "Universe"
/// section via the D67 builder. At launch: IVV-CSV primary + Wikipedia cross-check, count sanity
/// [495,510] (D49). The S&amp;P 100 slice (<see cref="Bootstrap"/>) is the forward universe through
/// Phase 4 (D70) and is wired in 1.5. Follows the …Options convention (SectionName + mutable
/// get/set defaults), matching ArenaOptions / WorkerOptions.
/// </summary>
public sealed class UniverseOptions
{
    public const string SectionName = "Universe";

    public string Index { get; set; } = "GSPC.INDX";

    /// <summary>S&amp;P 500 fail-closed band [min,max] (D35). NOTE: this is MembershipCountSanity —
    /// distinct from Bootstrap.CountSanity ([99,103] for the S&amp;P 100 slice). INTEGRATIONS §2
    /// mis-cites the 495–510 band as Bootstrap.CountSanity; CONFIG_REFERENCE is authoritative.</summary>
    public int[] MembershipCountSanity { get; set; } = [495, 510];

    public string MembershipPrimary { get; set; } = "ivv_csv";
    public string MembershipCrossCheck { get; set; } = "wikipedia";
    public string SectorSource { get; set; } = "ivv_csv";
    public string HistoricalMembershipSource { get; set; } = "community_csv";

    public UniverseBootstrapOptions Bootstrap { get; set; } = new();
}

/// <summary>The D70 S&amp;P 100 launch slice (Universe.Bootstrap): the forward universe through
/// Phase 4 sign-off. OEF-CSV primary + Wikipedia S&amp;P 100 cross-check, count sanity [99,103].
/// Consumed by the backfill CLI + Stage-1 eligibility; wired in 1.5.</summary>
public sealed class UniverseBootstrapOptions
{
    public string Universe { get; set; } = "sp100";
    public string MembershipPrimary { get; set; } = "oef_csv";
    public string MembershipCrossCheck { get; set; } = "wikipedia_sp100";
    public int[] CountSanity { get; set; } = [99, 103];
}
