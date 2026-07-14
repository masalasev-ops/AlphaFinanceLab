namespace AlphaLab.Data.Providers;

/// <summary>
/// The bar cross-check seam (FR-6): a secondary daily-bar source used by the rotating-sample quality
/// gate to catch systematic EODHD errors (<c>Data.BarCrossCheckSampleSize</c> names/day within
/// <c>Data.BarCrossCheckTolerancePct</c>). One method — fetch the cross-check source's daily closes
/// for a symbol over a window — so the quality gate can compare same-date closes and flag a
/// <c>CrossCheckMismatch</c>. The concrete source at launch is Alpaca (INTEGRATIONS §6).
/// </summary>
public interface IBarCrossCheckProvider
{
    /// <summary>Daily bars for <paramref name="symbol"/> over [from, to] from the cross-check source,
    /// shaped as <see cref="EodBar"/> so a same-date close comparison is apples-to-apples.</summary>
    Task<IReadOnlyList<EodBar>> GetDailyBarsAsync(string symbol, string from, string to, CancellationToken ct = default);
}

/// <summary>
/// DORMANT (D49 launch config): the Alpaca bar cross-check (INTEGRATIONS §6 — the IEX free tier over
/// <c>APCA-API-KEY-ID</c>/<c>APCA-API-SECRET-KEY</c>). It is OPTIONAL by design — the launch deployment
/// has no Alpaca account (the Secrets Alpaca pair is optional, NFR-4), so the seam is built but never
/// wired active in 1.7, and <c>Data.BarCrossCheckSampleSize</c>/<c>TolerancePct</c> stay inert. The
/// reconciliation half of FR-6 (adj/raw factor vs the event feed) IS fully built in
/// <see cref="AlphaLab.Data.Services.DataQualityGate"/>; only this external cross-check is deferred.
/// <see cref="GetDailyBarsAsync"/> fails loud rather than silently returning an empty set that would
/// read as "cross-check agreed". Activation (register this provider + the rotating-sample compare) is
/// a config/wiring change once the optional Alpaca keys are provided — not new gate logic.
/// </summary>
public sealed class AlpacaBarCrossCheck : IBarCrossCheckProvider
{
    public const string DormantReason =
        "AlpacaBarCrossCheck is dormant (D49): the launch deployment has no Alpaca account (the Secrets " +
        "Alpaca pair is optional, NFR-4). Provide Secrets:AlpacaKeyId/AlpacaSecretKey and wire the " +
        "provider to activate the rotating-sample bar cross-check (FR-6).";

    public Task<IReadOnlyList<EodBar>> GetDailyBarsAsync(
        string symbol, string from, string to, CancellationToken ct = default) =>
        throw new NotSupportedException(DormantReason);
}
