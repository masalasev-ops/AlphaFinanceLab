using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AlphaLab.Api.Tests;

public class ApiEndpointsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    [Fact]
    public async Task OpenApiJson_IsServedUnconditionally_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ScalarUi_IsServedUnconditionally_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/scalar/v1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_RedirectsToScalar()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/swagger");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/scalar/v1", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Strategies_Returns_NoRunYet_Stamp_WithSnakeCaseNullKeys()
    {
        var client = _factory.CreateClient();
        var json = await client.GetStringAsync("/api/v1/strategies");

        using var doc = JsonDocument.Parse(json);
        var stamp = doc.RootElement.GetProperty("stamp");

        Assert.Equal("no_run_yet", stamp.GetProperty("status").GetString());

        // The keys are literally run_id / watermark / as_of (not runId), and null is EMITTED (D66).
        Assert.True(stamp.TryGetProperty("run_id", out var runId));
        Assert.Equal(JsonValueKind.Null, runId.ValueKind);
        Assert.True(stamp.TryGetProperty("watermark", out var watermark));
        Assert.Equal(JsonValueKind.Null, watermark.ValueKind);
        Assert.True(stamp.TryGetProperty("as_of", out var asOf));
        Assert.Equal(JsonValueKind.Null, asOf.ValueKind);
        Assert.False(stamp.TryGetProperty("runId", out _));
    }

    [Fact]
    public async Task Replay_ReadModel_IsAlwaysQuarantined()
    {
        var client = _factory.CreateClient();
        var json = await client.GetStringAsync("/api/v1/replay");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("quarantined").GetBoolean());
    }

    [Fact]
    public async Task UnknownRoute_ReturnsD60ErrorEnvelope_404()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":", json);
        Assert.Contains("\"code\":\"not_found\"", json);
    }
}
