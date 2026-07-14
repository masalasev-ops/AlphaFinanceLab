namespace AlphaLab.Data.Providers;

/// <summary>
/// DORMANT (D49): EODHD fundamentals (General::Sector/Industry) as a sector source. The endpoint is
/// off the launch "All World" tier, so at launch sectors come from the IVV/OEF holdings CSV's GICS
/// Sector column (Universe.SectorSource=ivv_csv) and industry stays null. Built now so the post-upgrade
/// flip to eodhd sectors (industry + a staleness alarm) is a config change, not new code. No live
/// fixture at launch; <see cref="GetSectorAsync"/> fails loud rather than returning a stale/empty value.
/// </summary>
public sealed class EodhdFundamentalsSectorProvider
{
    public const string DormantReason =
        "EodhdFundamentalsSectorProvider is dormant (D49): EODHD /fundamentals is off the launch tier. " +
        "Sectors come from the IVV/OEF holdings CSV; activate via Universe.SectorSource='eodhd' post-upgrade.";

    public Task<string?> GetSectorAsync(string symbol, CancellationToken ct = default) =>
        throw new NotSupportedException(DormantReason);
}
