using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlphaLab.Worker.Pipeline;

/// <summary>
/// The D53 daily pipeline's composition, in ONE place so every host that runs a trading day composes
/// it identically (v1.9.37 / checkpoint 3.5.1).
///
/// This exists because `reproduce-day` must re-run a past session through the SAME graph the forward
/// Worker used. If the reproduce path hand-assembled its own registrations, the two would drift — a
/// config bind added to Program.cs and forgotten here would make the reproduction quietly compare a
/// DIFFERENT pipeline to the committed one, and the NFR-1 proof would be measuring the wrong thing.
/// Sharing the composition makes the proof structural.
///
/// What is deliberately NOT here: the market-data / regime providers and the TimeProvider. Those are
/// exactly the axes the two hosts must differ on — the forward Worker binds EODHD + the system clock,
/// reproduce binds the stored-history providers + a clock pinned to the run's watermark. Everything
/// else (options binds, data, membership, Stage 1, the orchestrator) is common.
/// </summary>
public static class PipelineComposition
{
    /// <summary>Register the options binds, data access, membership graph, Stage 1 and the daily
    /// orchestrator. The CALLER supplies <see cref="TimeProvider"/>, <c>IMarketDataProvider</c> and
    /// <c>IRegimeProxyProvider</c>.</summary>
    public static IServiceCollection AddDailyPipelineCore(
        this IServiceCollection services,
        IConfiguration configuration,
        ArenaOptions arena,
        string connectionString,
        bool ensureDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(arena);

        services.AddSingleton(arena);
        services.AddAlphaLabData(connectionString, arena.Id, ensureDirectory);

        // CONFIG binds (finding F): the CONSUMING phase owns the bind, and the BOUND options must be
        // registered BEFORE AddAlphaLabMembership so its TryAddSingleton defaults are no-ops — otherwise
        // Data (D77 gate), Calendar, CorporateActions (findings B/C), Regime (D50) and Costs (D43) would
        // silently fall back to unbound defaults.
        var regimeOptions = Bind<RegimeOptions>(configuration, RegimeOptions.SectionName);
        services.AddSingleton(Bind<DataQualityOptions>(configuration, DataQualityOptions.SectionName));
        services.AddSingleton(Bind<CalendarOptions>(configuration, CalendarOptions.SectionName));
        services.AddSingleton(Bind<CorporateActionsOptions>(configuration, CorporateActionsOptions.SectionName));
        services.AddSingleton(Bind<CostsOptions>(configuration, CostsOptions.SectionName));
        // Phase 3: the random control populations compute inside the daily Stage-2 write (3.3), and the
        // 21-day evaluation + D51 allocator run post-commit (3.4/3.7) — the Worker is their consuming phase.
        services.AddSingleton(Bind<PopulationsOptions>(configuration, PopulationsOptions.SectionName));
        services.AddSingleton(Bind<GateOptions>(configuration, GateOptions.SectionName));
        services.AddSingleton(Bind<AllocatorOptions>(configuration, AllocatorOptions.SectionName));
        services.AddAlphaLabMembership(regimeOptions);

        // UniverseOptions bind + the rule-22 slice scope (Phase 4 / checkpoint 4.3 — this WAS the
        // "D70-widening job" finding F deferred). Once the historical S&P 500 membership lands,
        // MembersAsOf(today) resolves ~500 names; the FORWARD universe must stay the S&P 100 slice
        // through Phase-4 sign-off, so the membership read is decorated with an intersection against
        // the pre-ingest slice snapshot while Universe:Bootstrap:Universe == "sp100". The post-sign-off
        // widen is the config flip; the REPLAY composition re-registers the RAW read (replay never
        // runs on the slice, rule 22).
        services.AddSingleton(Bind<UniverseOptions>(configuration, UniverseOptions.SectionName));
        services.AddScoped<IIndexMembershipRead>(sp => new SliceScopedMembershipRead(
            new IndexMembershipReadService(sp.GetRequiredService<AlphaLabDbContext>()),
            sp.GetRequiredService<AlphaLabDbContext>(),
            sp.GetRequiredService<UniverseOptions>()));

        services.AddScoped<Stage1Fetch>();
        services.AddScoped<DailyPipeline>();
        // The evaluation cadence runs by default; ONLY the seeding backtest engine overrides (4.10).
        services.TryAddSingleton(new PipelineEvaluationToggle());
        return services;
    }

    private static T Bind<T>(IConfiguration configuration, string section) where T : new() =>
        configuration.GetSection(section).Get<T>() ?? new T();
}
