using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlphaLab.Core.Llm;

namespace AlphaLab.Evaluation.Ai;

/// <summary>
/// Builds and validates a D80 context pack.
///
/// **Lives in AlphaLab.Evaluation, not AlphaLab.Llm**, because a pack may be assembled ONLY through the
/// versioned read services / <c>IFeatureView</c> — which live in AlphaLab.Data, a project the `ci.ps1`
/// reference graph forbids AlphaLab.Llm from touching. `Evaluation/Ai/` also sits outside the
/// descriptive-only path grep (`Evaluation/{Allocator,Gate,Candidates,Power}` + `Core/Funnel`), so the
/// signal digest can be read here without tripping the Signal Library's boundary guard.
///
/// **Validation happens at CONSTRUCTION, not afterwards.** A leaked pack that exists is a leaked pack
/// that can be sent; a check that runs after the object is built leaves a window in which the wrong thing
/// is a valid object. Both D104 assertions are enforced here:
/// <list type="number">
/// <item><b>Closure</b> — a field not on the whitelist throws.</item>
/// <item><b>Admissibility</b> — a field whose <c>observed_at</c> is later than the simulated as-of throws,
/// checked PER FIELD.</item>
/// </list>
/// </summary>
public sealed class ContextPackBuilder(string recipeVersion)
{
    /// <summary>Deterministic JSON: fields are emitted in WHITELIST order, not insertion order. Byte
    /// identity is a requirement (<c>FX-PackWatermark</c>), and insertion order is a property of the
    /// builder's control flow — which a refactor can change without changing a single value.</summary>
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public ContextPack Build(
        string seat, string? strategyId, string asOf, string watermark, IReadOnlyList<PackField> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seat);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);
        ArgumentNullException.ThrowIfNull(fields);

        if (!AiSeat.All.Contains(seat))
        {
            throw new PackViolationException(
                $"Unknown seat '{seat}' — ai_context_packs.seat is CHECK-constrained to " +
                $"({string.Join(", ", AiSeat.All)}) and a pack for an unknown seat could not be stored.");
        }

        Validate(asOf, fields);

        // Whitelist order, and one entry per name: a duplicate would make the JSON depend on which copy
        // won, which is a byte-identity hazard rather than a merely untidy input.
        var byName = new Dictionary<string, PackField>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            if (!byName.TryAdd(f.Name, f))
            {
                throw new PackViolationException(
                    $"Duplicate pack field '{f.Name}' — a pack must have exactly one value per field, " +
                    "or its serialization depends on which copy won.");
            }
        }

        var obj = new JsonObject();
        foreach (var name in PackWhitelist.Allowed.Order(StringComparer.Ordinal))
        {
            if (byName.TryGetValue(name, out var f))
            {
                obj[name] = f.Value is null ? null : JsonSerializer.SerializeToNode(f.Value, Json);
            }
        }

        var packJson = obj.ToJsonString();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(packJson)));

        return new ContextPack(
            seat, strategyId, asOf, watermark, recipeVersion,
            [.. fields], packJson, hash, EstimateTokens(packJson));
    }

    /// <summary>The two D104 assertions. Public so a test can exercise them directly, and so a caller
    /// that assembles fields elsewhere can fail early rather than at serialization.</summary>
    public static void Validate(string asOf, IReadOnlyList<PackField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        foreach (var f in fields)
        {
            // (2) CLOSURE. Note the direction: an unknown field FAILS rather than being dropped, so the
            // invariant cannot decay silently as the pack grows (§23.8.2).
            if (!PackWhitelist.Allowed.Contains(f.Name))
            {
                throw new PackViolationException(
                    $"Pack field '{f.Name}' is not on the D104 whitelist. A field added to the builder and " +
                    "not to the whitelist FAILS rather than passing silently — add it to PackWhitelist " +
                    "deliberately, or do not put it in a pack. Fields that judge AI output (any proposal " +
                    "score) and prior AI decisions are barred outright by D110 R1 and rule 32.");
            }

            // (1) ADMISSIBILITY, per field. A pack assembled at the right watermark can still hold ONE
            // field resolved through a path that ignored it, which a pack-level check would not see.
            if (f.ObservedAt is { Length: > 0 } observed
                && string.CompareOrdinal(observed, asOf) > 0)
            {
                throw new PackViolationException(
                    $"Pack field '{f.Name}' carries observed_at '{observed}', later than the simulated " +
                    $"as-of '{asOf}'. This is the D104 leakage invariant: leakage into a pack is invisible " +
                    "in every downstream number and would invalidate the twin comparison that prices the seat.");
            }
        }
    }

    /// <summary>Rough token estimate for <c>ai_context_packs.token_estimate</c> — provenance, never
    /// billing. Reported usage always comes from the API.</summary>
    public static int EstimateTokens(string json) =>
        string.IsNullOrEmpty(json) ? 0 : (int)Math.Ceiling(json.Length / 3.5);
}
