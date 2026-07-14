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
    public static IServiceCollection AddAlphaLabMembership(this IServiceCollection services)
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
        services.TryAddSingleton(new CalendarOptions());
        services.AddScoped<ICalendarService, CalendarService>();
        services.AddScoped<ICalendarSeeder, CalendarSeeder>();
        services.TryAddSingleton(new RegimeOptions());
        services.AddScoped<IRegimeProxyIngestion, RegimeProxyIngestion>();
        services.AddScoped<IRegimeProxyReadiness, RegimeProxyReadiness>();
        return services;
    }
}
