using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlphaLab.Core.Json;

/// <summary>
/// The one JSON contract for the whole system (D60). snake_case property names and
/// snake_case string enums, applied identically by the API's HTTP serializer and by any
/// standalone serialization.
///
/// Deliberately does NOT set <c>DefaultIgnoreCondition = WhenWritingNull</c>: the D66
/// read-model stamp must emit <c>run_id: null</c> (and watermark/as_of null) in the
/// no_run_yet state, so dropping nulls would silently break the contract.
/// </summary>
public static class AlphaLabJson
{
    /// <summary>Apply the AlphaLab JSON conventions to an existing options object
    /// (used to configure the framework's HTTP JSON options in the API).</summary>
    public static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        // NOTE: intentionally no DefaultIgnoreCondition — nulls are emitted (D66).
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }

    /// <summary>A ready-made options instance for standalone (de)serialization and tests.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Apply(options);
        return options;
    }
}
