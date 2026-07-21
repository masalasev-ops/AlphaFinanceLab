using AlphaLab.Data.Http;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlphaLab.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AlphaLabDbContext"/> against the arena-namespaced SQLite file.
    /// </summary>
    /// <param name="ensureDirectory">
    /// true (default) — writers (the Worker, the EF design-time factory) resolve the path AND
    /// create the store's directory (D59: creating the store is a writer's job).
    /// false — readers (the API) resolve the path only and NEVER touch the filesystem at boot,
    /// so the API never creates the real DB directory (v1.9.6).
    /// </param>
    public static IServiceCollection AddAlphaLabData(
        this IServiceCollection services,
        string connectionString,
        string arenaId,
        bool ensureDirectory = true)
    {
        var resolved = ensureDirectory
            ? DbPathResolver.Resolve(connectionString, arenaId)
            : DbPathResolver.ResolvePath(connectionString, arenaId);

        // Fail closed at the composition root, readers included: a relative Data Source would give this
        // process its OWN database under its OWN working directory instead of the arena's store (rule 10).
        // Resolve() already checks for writers; this covers the reader branch (the Api) on the same terms.
        DbPathResolver.RequireAbsoluteStorePath(resolved);

        services.AddDbContext<AlphaLabDbContext>(options => options.UseSqlite(resolved));
        return services;
    }

    /// <summary>
    /// Registers the writer-side data-foundation building blocks (FR-4/FR-5/FR-6): the resilient HTTP
    /// client, a default no-op raw cache, the security master, the reconciler, sector ingestion, and
    /// the data-quality gate. This establishes the provider-wiring convention; the concrete membership
    /// providers (<c>ISharesHoldingsMembershipProvider</c>, <c>WikipediaMembershipCrossCheck</c>) and
    /// their options / raw-cache root are wired by the backfill CLI (1.10), which owns the config + the
    /// archive directory. The dormant Alpaca cross-check (<c>AlpacaBarCrossCheck</c>) is intentionally
    /// NOT registered — it activates via CLI wiring once the optional Alpaca keys exist (FR-6). Not
    /// registered by the read-only API path.
    /// </summary>
    /// <param name="regimeOptions">
    /// The bound <see cref="RegimeOptions"/> (finding F). CONFIG key rule 7: the CONSUMING phase owns
    /// the bind, and FR-26 (the regime-label service, checkpoint 2.8) is the first consumer of
    /// <c>Regime.*</c>. The composition root binds the section — <c>config.GetSection(RegimeOptions.
    /// SectionName).Get&lt;RegimeOptions&gt;()</c> — and passes the result here; without it every
    /// <c>Regime:*</c> value was silently ignored (an unbound <c>new RegimeOptions()</c>). Null keeps the
    /// defaults, so existing callers are unaffected. The sibling <c>DataQualityOptions</c> bind lands with
    /// its consumer at 2.10 (D77 wiring) and <c>CalendarOptions</c> with the catch-up calendar; both keep
    /// their default instances until then. <c>UniverseOptions</c> is deliberately not registered at all —
    /// wiring it IS the D70-widening proposal, not Phase-2 work.
    /// </param>
    public static IServiceCollection AddAlphaLabMembership(this IServiceCollection services, RegimeOptions? regimeOptions = null)
    {
        services.AddSingleton<IResilientHttpClient>(_ => new ResilientHttpClient(new HttpClient()));
        services.TryAddSingleton<IRawCache>(NullRawCache.Instance);
        services.AddScoped<ISecurityMaster, SecurityMaster>();
        services.AddScoped<IMembershipReconciler, MembershipReconciler>();
        services.AddScoped<IHistoricalMembershipIngestion, HistoricalMembershipIngestion>();
        services.AddScoped<IIndexMembershipRead, IndexMembershipReadService>();
        services.AddScoped<ISectorIngestion, SectorIngestion>();
        services.TryAddSingleton(new DataQualityOptions());
        services.AddScoped<IDataQualityGate, DataQualityGate>();
        services.AddScoped<IDataQualityFlagStore, DataQualityFlagStore>();  // D77 — the persistence sink for gate flags
        services.TryAddSingleton(new CalendarOptions());
        services.AddScoped<ICalendarService, CalendarService>();
        services.AddScoped<ICalendarSeeder, CalendarSeeder>();
        services.TryAddSingleton(regimeOptions ?? new RegimeOptions());  // finding F — bound Regime.* flows in
        services.AddScoped<IRegimeProxyIngestion, RegimeProxyIngestion>();
        services.AddScoped<IRegimeProxyReadiness, RegimeProxyReadiness>();
        services.AddScoped<IRegimeLabelService, RegimeLabelService>();  // FR-26/D50 — the PIT regime label (2.8)
        services.AddScoped<ILedgerStore, LedgerStore>();   // Phase 2 (2.2) — the ledger persistence seam
        // Phase 2 is the first CONSUMER of these read paths (the funnel + the CA ledger), so it owns the
        // binds (CONFIG key rule 7 for services): the watermarked bar read (D40/D78) and the watermarked
        // corporate-action read (D76). Both are pure readers over the versioned stores.
        services.AddScoped<IBarReadService, BarReadService>();
        services.AddScoped<ICorporateActionReadService, CorporateActionReadService>();
        services.TryAddSingleton(new AlphaLab.Core.Config.CorporateActionsOptions()); // findings B/C — delist haircut, spin-off liquidation
        services.AddScoped<CorporateActionApplier>();      // 2.6/2.7 — §13.6 ledger (dividend/split/ticker/freeze; mergers/spin-off/delist)
        return services;
    }
}
