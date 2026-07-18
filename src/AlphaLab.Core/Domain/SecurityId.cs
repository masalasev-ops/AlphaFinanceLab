using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlphaLab.Core.Domain;

/// <summary>
/// The permanent internal identity of a security (D39, hard rule 2). Tickers are time-ranged
/// display aliases resolved through ticker_history; they are NEVER an identity.
///
/// This is a wrapper rather than a bare long on purpose: catalog §2 requires that a model's
/// score keys are security ids, and a bare long would let a ticker-derived int, an account_id,
/// or a run_id bind to the same parameter silently. Wrapping makes "keys are security_ids,
/// never raw tickers" a COMPILE-TIME fact instead of a code-review convention.
///
/// Deliberately NOT the same trade as money (see the ledger, where plain decimal is used): a
/// money wrapper would buy no correctness over decimal's own exactness, whereas this wrapper
/// buys type-separation between three interchangeable longs.
/// </summary>
[JsonConverter(typeof(SecurityIdJsonConverter))]
public readonly record struct SecurityId(long Value)
{
    public override string ToString() => Value.ToString();

    /// <summary>Explicit, so a raw long can never bind implicitly (that would defeat the point).</summary>
    public static explicit operator long(SecurityId id) => id.Value;
}

/// <summary>
/// Serializes <see cref="SecurityId"/> as a bare JSON number rather than the default
/// <c>{"value": 42}</c> wrapper object.
///
/// This is not cosmetic. `decisions.stage_json` is the "Why this trade" provenance a human reads a
/// year after the fact, and it is also the carrier that hands orders decided at close T to the run
/// that fills them at open T+1. Both audiences are better served by `"security_id": 42` than by a
/// nested object that exists only because the id happens to be a struct in C#.
/// </summary>
public sealed class SecurityIdJsonConverter : JsonConverter<SecurityId>
{
    public override SecurityId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, SecurityId value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);

    /// <summary>Also valid as a dictionary KEY (JSON object keys are strings), so a
    /// score-by-security map round-trips as `{"42": 0.9}` instead of failing to serialize.</summary>
    public override SecurityId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(long.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, SecurityId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
