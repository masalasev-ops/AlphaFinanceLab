using System.Text.Json;
using System.Text.Json.Nodes;
using AlphaLab.Core.Json;

namespace AlphaLab.Core.Domain;

/// <summary>
/// The ONE reader/writer for <c>strategies.config_json</c> (D133).
///
/// It exists because the column had **four writers and no reader**: <c>DummyRoster</c> serialized a typed
/// <see cref="StrategyConfig"/> with <see cref="AlphaLabJson.Options"/>, <c>ReplayRunner</c> wrote an
/// anonymous object WITHOUT those options, <c>CandidateFactory</c> stamped a marker through untyped
/// <c>JsonNode</c> (also without them), and the API passed a caller string straight through. One column,
/// several conventions, and nothing that could read any of them back — so D17's "frozen params" was a
/// promise with no witness.
///
/// **TOLERANT BY CONSTRUCTION (D133).** <see cref="Read"/> never throws on a historical payload: an
/// empty string, <c>{}</c>, or an anonymous plant object all deserialize to a config whose typed members
/// take their defaults and whose unknown keys are PRESERVED for round-tripping. That tolerance is not
/// laxity — D17 forbids re-serializing over a frozen row, so a reader that refused an old shape would
/// make the column unreadable exactly where it is guaranteed unrewritable.
///
/// **BYTE-STABLE ON WRITE.** Keys are emitted in ordinal order at every level, on
/// <c>SignalRegistrar</c>'s recorded precedent: *a provenance record whose bytes wobble cannot back a
/// determinism claim.* A dictionary's enumeration order is not a contract; the frozen bytes are.
/// </summary>
public static class StrategyConfigJson
{
    /// <summary>The unregistered marker key (D52/rule 16), snake_case per the D60 contract.</summary>
    public const string UnregisteredKey = "unregistered";

    /// <summary>
    /// Serialize a config to its frozen bytes: typed members through <see cref="AlphaLabJson.Options"/>,
    /// then every key ordered so two runs of the same config produce identical bytes.
    /// </summary>
    public static string Write(StrategyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var node = JsonSerializer.SerializeToNode(config, AlphaLabJson.Options) as JsonObject
                   ?? new JsonObject();
        return Canonicalize(node).ToJsonString(AlphaLabJson.Options);
    }

    /// <summary>
    /// Read a frozen payload. Returns null when the payload carries no typed config at all (an empty
    /// string, or JSON that is not an object) — the caller decides whether that is a defect, because for
    /// a pre-D133 plant row it is simply the truth.
    /// </summary>
    public static StrategyConfig? Read(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(configJson); }
        catch (JsonException) { return null; }
        if (node is not JsonObject obj) return null;

        // The typed members are all optional on read: a historical row has none of them, and `required`
        // on the record applies to construction in C#, never to what a frozen row happens to contain.
        try
        {
            return new StrategyConfig
            {
                Seed = obj.TryGetPropertyValue("seed", out var s) && s is not null ? s.GetValue<int>() : 0,
                Selection = ReadOr(obj, "selection", SelectionRule.TopN(1)),
                Sizing = ReadOr(obj, "sizing", SizingMode.Equal),
                Params = ReadMap<double>(obj, "params"),
                Frozen = ReadMap<string>(obj, "frozen"),
                FrozenSets = ReadSets(obj, "frozen_sets"),
                Horizon = ReadOr<HoldingHorizon?>(obj, "horizon", null),
                Unregistered = obj.TryGetPropertyValue(UnregisteredKey, out var u) && u is not null
                               && u.GetValue<bool>(),
            };
        }
        catch (JsonException)
        {
            // A payload shaped like a config but not parseable as one is still readable AS A ROW; the
            // caller gets null and can report it, rather than the daily run dying on a legacy fixture.
            return null;
        }
    }

    /// <summary>
    /// Stamp the D52 unregistered marker THROUGH the shape (D133 closes the two-writers-one-key hole:
    /// the typed <see cref="StrategyConfig.Unregistered"/> and a raw <c>JsonNode</c> marker both wrote
    /// this key, by different conventions). A payload that is not a readable config still gets the
    /// marker — an unregistered plant row must stay honestly marked.
    ///
    /// **Stamps at the NODE level, and that is the fix, not a retreat from D133's framing (6.3, finding
    /// 391).** "Through the shape" means ONE writer under ONE convention — the canonical key order and
    /// <see cref="AlphaLabJson.Options"/>, both applied here. It never meant a typed round-trip, and a
    /// typed round-trip is what made this lossy: <see cref="Read"/> populates only the declared members,
    /// <see cref="StrategyConfig"/> has nowhere to hold anything else, so <c>Write(Read(x))</c> silently
    /// DROPPED every unknown key — the opposite of the TOLERANT property this class claims. The API
    /// accepts caller-supplied <c>config_json</c>, so a candidate created through it lost whatever it
    /// froze that the typed shape does not name.
    /// </summary>
    public static string WithUnregisteredMarker(string? configJson) =>
        Stamp(configJson, obj => obj[UnregisteredKey] = true);

    /// <summary>
    /// Stamp a FROZEN STRING into the <c>frozen</c> bag — the same one-writer-one-convention discipline
    /// as <see cref="WithUnregisteredMarker"/>, generalised at 6.3 when the cadence-family declaration
    /// became the second thing a factory needs to write.
    ///
    /// **Only ever called on a config being CREATED.** D17 forbids re-serializing over a frozen row, and
    /// this method cannot know whether its argument is one — so the obligation sits with the caller,
    /// exactly as it does for the unregistered marker. An EXISTING value for <paramref name="key"/> is
    /// never overwritten: a caller that meant to change a frozen param is describing a FORK (rule 8),
    /// and silently honouring it here would be that fork happening without the new strategy_id or the
    /// trial it owes.
    /// </summary>
    public static string WithFrozen(string? configJson, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        return Stamp(configJson, obj =>
        {
            if (obj["frozen"] is not JsonObject bag)
            {
                bag = [];
                obj["frozen"] = bag;
            }
            bag[key] ??= value;
        });
    }

    /// <summary>Apply <paramref name="mutate"/> to the payload's object form and re-emit it canonically —
    /// every key that was there stays there, in one key order, under one set of options.</summary>
    private static string Stamp(string? configJson, Action<JsonObject> mutate)
    {
        var obj = ParseObjectOrEmpty(configJson);
        mutate(obj);
        return Canonicalize(obj).ToJsonString(AlphaLabJson.Options);
    }

    /// <summary>
    /// The polymorphic type discriminator used throughout the domain — `ExitPolicy` and
    /// `HoldingHorizon` both declare <c>TypeDiscriminatorPropertyName = "kind"</c>.
    /// </summary>
    private const string Discriminator = "kind";

    /// <summary>
    /// Order every key at every level, so the frozen bytes are stable — EXCEPT the polymorphic
    /// discriminator, which is hoisted to first position.
    ///
    /// **That exception is load-bearing, not cosmetic.** System.Text.Json requires a polymorphic type's
    /// discriminator to be the FIRST property of its object; sorting keys ordinally moved <c>kind</c>
    /// after <c>days_</c> and deserialization then failed with *"must specify a type discriminator"* —
    /// the payload contained it, in the wrong place. Byte-stability and polymorphism both hold only if
    /// the discriminator leads and everything after it is ordered.
    /// </summary>
    private static JsonNode Canonicalize(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var ordered = new JsonObject();
                if (o.TryGetPropertyValue(Discriminator, out var kind) && kind is not null)
                {
                    ordered[Discriminator] = Canonicalize(kind.DeepClone());
                }
                foreach (var kv in o.Where(k => k.Key != Discriminator).OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    ordered[kv.Key] = kv.Value is null ? null : Canonicalize(kv.Value.DeepClone());
                }
                return ordered;
            }
            case JsonArray a:
            {
                // Element ORDER is part of a frozen set's value and is never sorted — only object keys are.
                var arr = new JsonArray();
                foreach (var item in a) arr.Add(item is null ? null : Canonicalize(item.DeepClone()));
                return arr;
            }
            default:
                return node.DeepClone();
        }
    }

    private static JsonObject ParseObjectOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static T ReadOr<T>(JsonObject obj, string name, T fallback) =>
        obj.TryGetPropertyValue(name, out var v) && v is not null
            ? v.Deserialize<T>(AlphaLabJson.Options) ?? fallback
            : fallback;

    private static IReadOnlyDictionary<string, T> ReadMap<T>(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var v) && v is JsonObject map
            ? map.Deserialize<Dictionary<string, T>>(AlphaLabJson.Options) ?? []
            : new Dictionary<string, T>();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSets(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var v) && v is JsonObject map &&
            map.Deserialize<Dictionary<string, List<string>>>(AlphaLabJson.Options) is { } raw)
        {
            return raw.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
        }
        return new Dictionary<string, IReadOnlyList<string>>();
    }
}
