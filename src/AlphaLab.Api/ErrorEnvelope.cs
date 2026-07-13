using System.Text.Json.Serialization;

namespace AlphaLab.Api;

/// <summary>
/// The uniform D60 error envelope: { error: { code, message, details? } }. Serialized with the
/// shared snake_case policy. Conventional status codes: 400 validation, 404, 409 conflict
/// (command-during-run), 422 domain-rule rejection, 503 dependency exhausted.
/// </summary>
public sealed record ErrorEnvelope(ErrorBody Error)
{
    public static ErrorEnvelope Of(string code, string message, object? details = null) =>
        new(new ErrorBody(code, message, details));
}

public sealed record ErrorBody(
    string Code,
    string Message,
    // "details?" — omitted from the JSON when absent (per-property override of the global policy,
    // which otherwise emits nulls so the D66 stamp keeps run_id:null).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Details = null);

/// <summary>Helpers producing D60-shaped error results with the conventional status codes.</summary>
public static class ApiResults
{
    public static IResult Error(int statusCode, string code, string message, object? details = null) =>
        Results.Json(new ErrorEnvelope(new ErrorBody(code, message, details)), statusCode: statusCode);

    public static IResult NotFound(string message = "Resource not found.") =>
        Error(StatusCodes.Status404NotFound, "not_found", message);
}
