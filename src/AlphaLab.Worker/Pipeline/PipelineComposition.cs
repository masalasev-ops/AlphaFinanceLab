using System.Globalization;
using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;
using AlphaLab.Data;
using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using AlphaLab.Llm;
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
        // NOTE: no IPostCommitStage is registered HERE. Stage 3 (the LLM) is added only by the FORWARD
        // composition (AddForwardLlmStage) — the replay and backtest graphs share this core and must be
        // provably model-free, which is what FR21_Replay_HasNoAnalysisPath asserts by ABSENCE rather than
        // by a guard. Registering it here "for convenience" is exactly the edit that would break that.
        return services;
    }

    /// <summary>
    /// Stage 3 of the D53 pipeline (FR-21/FR-29), registered by the **forward** composition only.
    ///
    /// This is the one place a model provider enters the graph. The replay composition never calls it, so
    /// a replay run cannot reach a model even by mistake: `FR21_Replay_HasNoAnalysisPath` asserts the
    /// absence of the registration rather than the behaviour of a runtime check, because a guard can be
    /// bypassed and a missing registration cannot.
    /// </summary>
    public static IServiceCollection AddForwardLlmStage(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var llm = Bind<LlmOptions>(configuration, LlmOptions.SectionName);
        services.AddSingleton(llm);

        var anthropic = new AnthropicTransportOptions
        {
            // D67: the ONLY source. No env vars, no user secrets.
            ApiKey = configuration["Secrets:AnthropicApiKey"] ?? "",
        };
        services.AddSingleton(anthropic);

        services.AddScoped<IAnalysisCache, AnalysisCacheStore>();
        services.AddScoped<ILlmBudgetLedger, LlmBudgetLedger>();
        services.AddScoped<IAdmittedNewsStore, AdmittedNewsStore>();

        services.AddScoped<IModelTransport>(sp => new AnthropicHttpTransport(
            sp.GetRequiredService<IResilientHttpSender>(),
            sp.GetRequiredService<AnthropicTransportOptions>()));

        // The budget decorator wraps the raw provider — composition is the ONLY place the unbudgeted
        // provider is visible, which is what makes the D24 rail unbypassable rather than merely present.
        services.AddScoped<IAnalysisProvider>(sp => new BudgetedAnalysisProvider(
            new AnthropicAnalysisProvider(
                sp.GetRequiredService<IModelTransport>(),
                sp.GetRequiredService<LlmOptions>()),
            sp.GetRequiredService<IAnalysisCache>(),
            sp.GetRequiredService<ILlmBudgetLedger>(),
            sp.GetRequiredService<LlmOptions>(),
            () => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        // Same shape for news: the D46 budget decorates the raw EODHD feed.
        services.AddScoped<INewsProvider>(sp => new BudgetedNewsProvider(
            new EodhdNewsProvider(
                sp.GetRequiredService<IResilientHttpClient>(),
                sp.GetRequiredService<EodhdOptions>()),
            sp.GetRequiredService<IAdmittedNewsStore>(),
            sp.GetRequiredService<LlmOptions>(),
            // The relevance filter matches SYMBOLS; membership is keyed by security_id, so resolve
            // through `securities`. Current membership (not as-of): the news read is a live operational
            // act on today's roster, not a point-in-time reconstruction.
            () => ResolveCurrentSymbols(sp)));

        services.AddScoped<IPostCommitStage, RegimeBriefStage>();
        return services;
    }

    /// <summary>Bare tickers of the arena's current members, for the D46 relevance filter.</summary>
    private static IReadOnlySet<string> ResolveCurrentSymbols(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AlphaLabDbContext>();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var ids = sp.GetRequiredService<IIndexMembershipRead>().MembersAsOf(today).ToHashSet();
        if (ids.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return db.Securities
            .Where(x => ids.Contains(x.SecurityId))
            .Select(x => x.CurrentSymbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static T Bind<T>(IConfiguration configuration, string section) where T : new() =>
        configuration.GetSection(section).Get<T>() ?? new T();
}
