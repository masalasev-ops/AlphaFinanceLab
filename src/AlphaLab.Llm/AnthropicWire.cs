using System.Text.Json;
using System.Text.Json.Nodes;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm;

/// <summary>
/// Request shaping and response parsing for the Anthropic Messages/Batches API (INTEGRATIONS §5).
///
/// Split from the provider so the wire format is unit-testable against captured payloads with no
/// transport in play — the same discipline the EODHD parse fixtures follow.
/// </summary>
public static class AnthropicWire
{
    /// <summary>Endpoint paths, relative to the API base (INTEGRATIONS §5).</summary>
    public const string MessagesPath = "/v1/messages";
    public const string BatchesPath = "/v1/messages/batches";

    /// <summary>
    /// Build the <c>params</c> object for one request.
    ///
    /// **The cache breakpoint sits on the LAST system block**, so tools+system are cached together and
    /// only the fresh user turn is charged. Render order is tools → system → messages, and caching is a
    /// PREFIX match, so the layering here is what makes the §23.2 economics hold rather than a style
    /// choice: L0+L1 go in <c>system</c>, L2 goes in the user turn, and nothing volatile precedes the
    /// breakpoint.
    ///
    /// **No sampling parameters are sent.** <c>temperature</c>/<c>top_p</c>/<c>top_k</c> do not exist on
    /// the pinned tier and are rejected with a 400 — behaviour is steered by prompt, not by knobs.
    /// </summary>
    public static JsonObject BuildParams(
        string model, PromptLayers prompt, int maxTokens, bool cacheStaticBlock)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var systemBlock = new JsonObject
        {
            ["type"] = "text",
            ["text"] = prompt.CacheablePrefix,
        };
        if (cacheStaticBlock)
        {
            systemBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        }

        return new JsonObject
        {
            ["model"] = model,
            // max_tokens caps thinking AND response text together on the pinned tier, so this is sized
            // with headroom by the caller; a limit sized for the answer alone truncates mid-thought.
            ["max_tokens"] = maxTokens,
            ["system"] = new JsonArray(systemBlock),
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt.Fresh,
                }),
        };
    }

    /// <summary>Build the batch-create body. <c>custom_id</c> is mandatory per request: results come back
    /// in ANY order and are correlated by it, never by position (INTEGRATIONS §5).</summary>
    public static string BuildBatchBody(
        IReadOnlyList<AnalysisRequest> requests,
        Func<AnalysisTask, string> modelFor,
        int maxTokens,
        bool cacheStaticBlock)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(modelFor);

        var arr = new JsonArray();
        foreach (var r in requests)
        {
            arr.Add(new JsonObject
            {
                ["custom_id"] = r.CustomId,
                ["params"] = BuildParams(modelFor(r.Task), r.Prompt, maxTokens, cacheStaticBlock),
            });
        }
        return new JsonObject { ["requests"] = arr }.ToJsonString();
    }

    /// <summary>Build a single synchronous message body (the interactive research-assistant path).</summary>
    public static string BuildMessageBody(
        string model, PromptLayers prompt, int maxTokens, bool cacheStaticBlock)
        => BuildParams(model, prompt, maxTokens, cacheStaticBlock).ToJsonString();

    /// <summary>The batch id from a create/poll response.</summary>
    public static string ReadBatchId(string json)
        => JsonNode.Parse(json)?["id"]?.GetValue<string>()
           ?? throw new InvalidOperationException("Batch response carried no 'id'.");

    /// <summary>True once <c>processing_status</c> is <c>ended</c>. Anything else means keep polling.</summary>
    public static bool IsBatchEnded(string json)
        => JsonNode.Parse(json)?["processing_status"]?.GetValue<string>() == "ended";

    /// <summary>Raw per-result usage, straight off the response. Authoritative — the pre-flight estimate
    /// is never used for reporting.</summary>
    public readonly record struct RawUsage(int Input, int Output, int CacheRead, int CacheWrite);

    /// <summary>One parsed batch result line, before costing.</summary>
    public readonly record struct ParsedResult(
        string CustomId, AnalysisOutcome Outcome, string RawOutput, RawUsage Usage, string Model, string? Detail);

    /// <summary>
    /// Parse the batch results stream. The endpoint returns **JSONL** — one JSON object per line, not a
    /// JSON array — so it is read line by line; blank lines are skipped.
    ///
    /// All four <c>result.type</c> values are handled. <c>errored</c> / <c>canceled</c> / <c>expired</c>
    /// become <see cref="AnalysisOutcome.Unavailable"/>: a missing read is a no-read day (D24), never an
    /// exception that would take the pipeline down with it.
    /// </summary>
    public static IReadOnlyList<ParsedResult> ParseBatchResults(string jsonl)
    {
        var outp = new List<ParsedResult>();
        if (string.IsNullOrWhiteSpace(jsonl)) return outp;

        foreach (var line in jsonl.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var node = JsonNode.Parse(trimmed);
            if (node is null) continue;

            var customId = node["custom_id"]?.GetValue<string>() ?? "";
            var result = node["result"];
            var type = result?["type"]?.GetValue<string>() ?? "errored";

            if (type != "succeeded")
            {
                // errored splits further (invalid_request is permanent, server errors are retryable) —
                // carried in Detail for the log, not branched on here: at batch scope the honest outcome
                // is the same either way, a no-read for that custom_id.
                var errType = result?["error"]?["type"]?.GetValue<string>();
                outp.Add(new ParsedResult(
                    customId, AnalysisOutcome.Unavailable, "", default, "",
                    errType is null ? type : $"{type}:{errType}"));
                continue;
            }

            outp.Add(ParseMessage(customId, result!["message"]));
        }
        return outp;
    }

    /// <summary>Parse a single message object (shared by the batch and synchronous paths).</summary>
    public static ParsedResult ParseMessage(string customId, JsonNode? message)
    {
        if (message is null)
        {
            return new ParsedResult(customId, AnalysisOutcome.Unavailable, "", default, "", "no message");
        }

        var model = message["model"]?.GetValue<string>() ?? "";
        var stopReason = message["stop_reason"]?.GetValue<string>();
        var u = message["usage"];
        var usage = new RawUsage(
            u?["input_tokens"]?.GetValue<int>() ?? 0,
            u?["output_tokens"]?.GetValue<int>() ?? 0,
            u?["cache_read_input_tokens"]?.GetValue<int>() ?? 0,
            u?["cache_creation_input_tokens"]?.GetValue<int>() ?? 0);

        // A refusal is a SUCCESSFUL HTTP response with stop_reason 'refusal' and empty or partial
        // content. Checked BEFORE reading content, because code that indexes content[0] unconditionally
        // breaks here — and it breaks on a 200, which is the part that makes it easy to miss.
        if (stopReason == "refusal")
        {
            var category = message["stop_details"]?["category"]?.GetValue<string>();
            return new ParsedResult(
                customId, AnalysisOutcome.Refused, "", usage, model,
                category is null ? "refusal" : $"refusal:{category}");
        }

        return new ParsedResult(customId, AnalysisOutcome.Succeeded, ConcatText(message["content"]), usage, model, null);
    }

    /// <summary>Concatenate every <c>text</c> block. Non-text blocks (thinking, tool use) are skipped —
    /// on the pinned tier thinking is on by default and its blocks carry empty text anyway.</summary>
    private static string ConcatText(JsonNode? content)
    {
        if (content is not JsonArray blocks) return "";
        var parts = new List<string>();
        foreach (var b in blocks)
        {
            if (b?["type"]?.GetValue<string>() == "text" && b["text"]?.GetValue<string>() is { } t)
            {
                parts.Add(t);
            }
        }
        return string.Join("", parts);
    }

    /// <summary>Stable SHA-256 of the exact prompt bytes sent, used as the <c>analysis_cache</c> key.
    /// Covers all three layers: a change to ANY of them is a different prompt and must miss the cache.</summary>
    public static string PromptHash(PromptLayers prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var payload = $"{prompt.StaticInstructions}{prompt.LessonSet}{prompt.Fresh}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(bytes);
    }
}
