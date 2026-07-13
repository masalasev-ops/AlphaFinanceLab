using AlphaLab.Core.Json;

namespace AlphaLab.Api;

/// <summary>
/// Converts any unhandled exception into the uniform D60 error envelope so no client ever sees a
/// raw framework error page. Endpoint-level validation returns typed 400/404/409/422/503 envelopes
/// directly; this is the catch-all backstop (500).
/// </summary>
public sealed class ErrorEnvelopeMiddleware(RequestDelegate next, ILogger<ErrorEnvelopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception; returning D60 error envelope.");
            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            var envelope = ErrorEnvelope.Of("internal_error", "An unexpected error occurred.");
            await context.Response.WriteAsJsonAsync(envelope, AlphaLabJson.Options);
        }
    }
}
