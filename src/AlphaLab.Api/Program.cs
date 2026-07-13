using AlphaLab.Api;
using AlphaLab.Core.Json;
using AlphaLab.Data;
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

// Unknown routes get the D60 envelope ({ error: { code:"not_found", … } }), not a framework 404 page.
app.MapFallback(() => ApiResults.NotFound()).ExcludeFromDescription();

app.Run();

/// <summary>Minimal liveness payload for GET /api/v1/health.</summary>
public sealed record HealthReadModel(string Status, string Arena);

/// <summary>Exposed so AlphaLab.Api.Tests can drive the app via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
