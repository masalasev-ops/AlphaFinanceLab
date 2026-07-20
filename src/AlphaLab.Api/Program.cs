using System.Globalization;
using AlphaLab.Api;
using AlphaLab.Core.Config;
using AlphaLab.Core.Json;
using AlphaLab.Data;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Candidates;
using AlphaLab.Evaluation.ReadModels;
using Microsoft.Extensions.Configuration;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// D67: the config builder is EXACTLY appsettings.json + appsettings.Secrets.json (optional).
// No env vars, no User Secrets. Clear the CreateBuilder defaults, then add the two files.
// Kestrel's bind address is carried by the "Urls" key in appsettings.json (finding 94/103) — there
// is no Api:Bind key. The committed value is http://127.0.0.1:5230 (localhost-only, http-only, D57).
builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: false);

var arenaId = builder.Configuration["Arena:Id"] ?? DbPathResolver.DefaultArenaId;
var connectionString = builder.Configuration.GetConnectionString("AlphaLab")
    ?? throw new InvalidOperationException("ConnectionStrings:AlphaLab is required in appsettings.json.");

// The API is a READER of the store plus bounded Phase-3 command writes (D59). ensureDirectory:false
// — a reader must NEVER create the store, and Phase 0 endpoints never open the DB (they return
// static read-models), so the API stays filesystem-free at boot.
builder.Services.AddAlphaLabData(connectionString, arenaId, ensureDirectory: false);

// The D58 read-model builders (AlphaLab.Evaluation) are the API's read side — pure projections; the API
// itself holds no statistics/thresholds/verdict logic (D57). The API is the consuming phase for the
// read-model options, so it owns their bind (finding F).
builder.Services.AddSingleton(builder.Configuration.GetSection(VerdictsOptions.SectionName).Get<VerdictsOptions>() ?? new VerdictsOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection(KpiOptions.SectionName).Get<KpiOptions>() ?? new KpiOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection(GateOptions.SectionName).Get<GateOptions>() ?? new GateOptions());
builder.Services.AddScoped<StrategiesReadModelBuilder>();
builder.Services.AddScoped<AllocationReadModelBuilder>();
builder.Services.AddScoped<CohortMaturationBuilder>();

// The bounded command surface (FR-32): the D52 CandidateFactory write + the D72 liveness 409 guard, so a
// command never races the Worker's daily write (completes the PARTIAL FR34_NoOverlappingWriters).
builder.Services.AddScoped<IWorkerLiveness, WorkerLivenessReader>();
builder.Services.AddSingleton(TimeProvider.System);

// One JSON contract everywhere (D60): snake_case property names + snake_case string enums, and
// nulls are emitted (the D66 stamp must ship run_id:null).
builder.Services.ConfigureHttpJsonOptions(o => AlphaLabJson.Apply(o.SerializerOptions));

builder.Services.AddOpenApi();

var corsOrigins = builder.Configuration.GetSection("Api:CorsAllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// D60 uniform error envelope as the outermost backstop.
app.UseMiddleware<ErrorEnvelopeMiddleware>();

// Cross-origin: the browser-served WASM client (http://localhost:5210) is cross-origin to the API
// even on localhost. Without this the Phase-0 DoD fails in the browser.
app.UseCors();

// OpenAPI + Scalar served UNCONDITIONALLY (localhost-only personal tool; the Phase-0 DoD + the API
// tests require both to return 200 regardless of environment — do NOT gate on IsDevelopment()).
app.MapOpenApi();                                 // framework default document: /openapi/v1.json
app.MapScalarApiReference();                       // default prefix serves the UI at /scalar/v1
app.MapGet("/swagger", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription(); // back-compat

var v1 = app.MapGroup("/api/v1");
v1.MapGet("/health", () => TypedResults.Ok(new HealthReadModel("ok", arenaId)))
    .WithName("Health").WithSummary("Liveness probe for the API process.");
v1.MapScreenReadEndpoints();

// ---- Bounded synchronous command: create / pre-register a candidate (D52/FR-28/FR-32) ----
const int staleThresholdSeconds = 300;   // WorkerOptions.StaleRunThresholdSeconds default (D72)
v1.MapPost("/candidates", async (
        CreateCandidateRequest req, AlphaLabDbContext db, IWorkerLiveness liveness,
        StrategiesReadModelBuilder strategies, TimeProvider clock, CancellationToken ct) =>
    {
        // Never race the daily write (D59/D72). A LIVE run ⇒ 409; a STALE flag is ignored (it is not a
        // real writer — the launch guard clears it).
        var worker = await liveness.GetAsync(staleThresholdSeconds, ct);
        if (worker.IsLive)
            return ApiResults.Error(409, "conflict", "A daily run is in progress — retry the command after it completes.");

        if (string.IsNullOrWhiteSpace(req.StrategyId))
            return ApiResults.Error(422, "unprocessable_entity", "strategy_id is required.");

        var createdOn = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var factory = new CandidateFactory(db);
        // ONE atomic transaction around both writes (D59 / the factory's own "writes via the caller's
        // transaction" contract): registering a hypothesis then failing to create the candidate (e.g. a
        // duplicate strategy_id ⇒ 422) must NOT leave an orphaned locked hypothesis behind.
        using var tx = db.Database.BeginTransaction();
        try
        {
            long? hypothesisId = req.Hypothesis is { } h
                ? factory.RegisterHypothesis(createdOn, h.Title, h.BodyMd, h.Metric, h.EvidenceWindowDays)
                : req.HypothesisEntryId;
            var spec = new CandidateSpec(req.StrategyId, req.Family ?? "unknown", req.ConfigJson ?? "{}",
                req.ExitPolicyJson ?? "{}", req.HoldingHorizonDays, req.ParentStrategyId);
            factory.CreateCandidate(spec, hypothesisId, req.Unregistered, createdOn, req.TrialKind ?? "new");
            tx.Commit();
            return Results.Ok(strategies.Build());   // the updated read-model (FR-32)
        }
        catch (InvalidOperationException ex)
        {
            // FR-28: missing hypothesis-or-flag (and the other pre-registration guards) ⇒ 422. The
            // transaction is disposed without commit, so any hypothesis INSERT is rolled back (no orphan).
            return ApiResults.Error(422, "unprocessable_entity", ex.Message);
        }
    })
    .WithName("CreateCandidate")
    .WithSummary("Create / pre-register a candidate (D52). 422 without a hypothesis-or-'unregistered' flag; 409 during a run.");

// Unknown routes get the D60 envelope ({ error: { code:"not_found", … } }), not a framework 404 page.
app.MapFallback(() => ApiResults.NotFound()).ExcludeFromDescription();

app.Run();

/// <summary>Minimal liveness payload for GET /api/v1/health.</summary>
public sealed record HealthReadModel(string Status, string Arena);

/// <summary>POST /api/v1/candidates body (D52). Supply EITHER an inline <see cref="Hypothesis"/> (or an
/// existing <see cref="HypothesisEntryId"/>) OR set <see cref="Unregistered"/> — else the factory 422s.</summary>
public sealed record CreateCandidateRequest(
    string StrategyId, string? Family, string? ConfigJson, string? ExitPolicyJson, int? HoldingHorizonDays,
    string? ParentStrategyId, bool Unregistered, long? HypothesisEntryId, HypothesisRequest? Hypothesis, string? TrialKind);

/// <summary>An inline pre-registered hypothesis (claim + metric + evidence window), locked on creation.</summary>
public sealed record HypothesisRequest(string Title, string BodyMd, string Metric, int EvidenceWindowDays);

/// <summary>Exposed so AlphaLab.Api.Tests can drive the app via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
