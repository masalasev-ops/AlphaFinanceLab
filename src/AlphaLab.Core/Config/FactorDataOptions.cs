namespace AlphaLab.Core.Config;

/// <summary>
/// The Ken French factor pull (CONFIG_REFERENCE "FactorData"; D41, checkpoint 6.6).
///
/// **`RefreshDayOfMonth` WAS DOCUMENTED AND BOUND BY NOTHING.** CONFIG_REFERENCE has carried
/// `"FactorData": { "RefreshDayOfMonth": 5 }` since v1.9, and before this checkpoint the token appeared
/// ZERO times in `src/` — no options class, no section binding, no reader. That is the finding-437
/// shape (a documented section an operator can edit while nothing reads it), and it is closed here by
/// binding the section rather than by deleting the key.
///
/// **THE TWO URLs ARE NEW KEYS, added to CONFIG_REFERENCE in the same commit** — that file's own rule
/// is "never invent a key; extend this file in the same PR". They are keys rather than constants on the
/// precedent already set for ingestion endpoints (`Backfill:WikipediaSp100Url`,
/// `Backfill:IvvHoldingsUrl`): a source URL that moves is an operator edit, not a rebuild.
///
/// Follows the …Options convention (SectionName + mutable get/set defaults mirroring CONFIG_REFERENCE).
/// </summary>
public sealed class FactorDataOptions
{
    public const string SectionName = "FactorData";

    /// <summary>Day of month the monthly refresh is due (D41). The library publishes with weeks of lag,
    /// so the exact day is not load-bearing — what matters is that it is monthly and not daily.</summary>
    public int RefreshDayOfMonth { get; set; } = 5;

    /// <summary>The 5-factor + RF daily zip (INTEGRATIONS §3). The `ftp/` segment is REQUIRED — without
    /// it the host serves an HTML page rather than a 404, which decodes without throwing and would reach
    /// the parser as a payload with no factor header (verified 2026-07-13).</summary>
    public string FiveFactorDailyUrl { get; set; } =
        "https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/ftp/F-F_Research_Data_5_Factors_2x3_daily_CSV.zip";

    /// <summary>The momentum (UMD) daily zip (INTEGRATIONS §3).</summary>
    public string MomentumDailyUrl { get; set; } =
        "https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/ftp/F-F_Momentum_Factor_daily_CSV.zip";

    /// <summary>Trading sessions the ingest may find missing inside the fetched window before the
    /// continuity check refuses. Not zero: the library's calendar and the NYSE calendar disagree on a
    /// handful of historical days, and a bar-for-bar match has never been the claim. The check exists to
    /// catch a TRUNCATED or misaligned file, which fails by orders of magnitude, not by one day.</summary>
    public int MaxMissingSessions { get; set; } = 5;
}
