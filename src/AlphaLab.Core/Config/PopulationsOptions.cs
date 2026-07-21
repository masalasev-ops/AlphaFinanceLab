namespace AlphaLab.Core.Config;

/// <summary>
/// The random control populations — the empirical null (CONFIG_REFERENCE "Populations", D36). Every
/// default here MIRRORS that file — it is the single source of truth; never hard-code a value that
/// belongs there.
///
/// Follows the …Options convention (SectionName + mutable get/set defaults matching CONFIG). The
/// CONSUMING phase owns the bind (finding F): registered in AlphaLab.Worker (the population engine,
/// checkpoint 3.3) and AlphaLab.Api (the read-model builders, checkpoint 3.13); unbound until then.
/// </summary>
public sealed class PopulationsOptions
{
    public const string SectionName = "Populations";

    /// <summary>M — members per cost-on cadence family (daily/banded/monthly).</summary>
    public int Size { get; set; } = 200;

    /// <summary>M for the cost-free (costs-off) display-only pure-noise band; never an S3 comparator.</summary>
    public int CostFreeSize { get; set; } = 50;

    /// <summary>Per-family deterministic seeds; a member's daily draw derives from (familySeed, memberIndex, date).</summary>
    public FamilySeedSet FamilySeeds { get; set; } = new();

    /// <summary>Members that keep full trade logs (the rest persist only the compact equity scalar).</summary>
    public int AuditFullLedgerSample { get; set; } = 5;

    /// <summary>finding 115: flag a strategy whose realized annualized turnover is outside ±this% of its
    /// matched population's median (renders the cost-match caveat on the S3 panel + StrategyRow read-model).</summary>
    public double TurnoverMatchTolerancePct { get; set; } = 30.0;

    /// <summary>
    /// The per-cadence family seeds. Phase 3 instantiates daily/banded/monthly (cost-on) + a cost-free
    /// daily twin (see PopulationSeeder). <see cref="Quarterly"/> is DORMANT this phase — the seat for
    /// Phase-8 quarterly/low-vol strategies, spawned on demand when the first such strategy enters (never
    /// speculatively). It is pre-listed so the seed stays stable when that day comes.
    /// </summary>
    public sealed class FamilySeedSet
    {
        public int Daily { get; set; } = 1001;
        public int Banded { get; set; } = 1002;
        public int Monthly { get; set; } = 1003;
        public int Quarterly { get; set; } = 1004;
    }
}
