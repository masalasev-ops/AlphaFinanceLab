using System.Text.Json.Nodes;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm.Tests;

/// <summary>
/// The Anthropic wire format (INTEGRATIONS §5), tested against literal payloads with no transport — the
/// same discipline as the captured EODHD parse fixtures.
/// </summary>
public class AnthropicWireTests
{
    [Fact]
    public void FR21_PromptCache_BreakpointSitsOnTheStaticBlock_NotTheFreshOne()
    {
        var p = AnthropicWire.BuildParams(
            "claude-opus-5", TestOptions.Prompt(), maxTokens: 4096, cacheStaticBlock: true);

        var system = p["system"]!.AsArray();
        Assert.Single(system);
        // L0+L1 are the cached prefix; the breakpoint is on them.
        Assert.Equal("ephemeral", system[0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Contains("INSTRUCTIONS + SCHEMA", system[0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("lesson set", system[0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        // The volatile block is in the user turn, AFTER the breakpoint. If it ever moved into the cached
        // prefix, every day would write a new cache entry and read none — the caching would silently
        // become a pure surcharge, which is the failure this asserts against.
        var content = p["messages"]!.AsArray()[0]!["content"]!.GetValue<string>();
        Assert.Equal("today's rows", content);
        Assert.DoesNotContain("today's rows", system[0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void PromptCache_CanBeDisabled()
    {
        var p = AnthropicWire.BuildParams(
            "claude-opus-5", TestOptions.Prompt(), maxTokens: 4096, cacheStaticBlock: false);
        Assert.Null(p["system"]!.AsArray()[0]!["cache_control"]);
    }

    [Fact]
    public void BuildParams_SendsNoSamplingParameters()
    {
        // temperature/top_p/top_k do not exist on the pinned tier and are rejected with a 400. Asserted
        // rather than assumed, because adding one is the kind of "harmless" edit that fails only at
        // runtime, on the live path, after the batch has already been paid for.
        var p = AnthropicWire.BuildParams(
            "claude-opus-5", TestOptions.Prompt(), maxTokens: 4096, cacheStaticBlock: true);

        Assert.Null(p["temperature"]);
        Assert.Null(p["top_p"]);
        Assert.Null(p["top_k"]);
    }

    [Fact]
    public void BuildBatchBody_CarriesACustomIdPerRequest()
    {
        var body = AnthropicWire.BuildBatchBody(
            [TestOptions.Request("a"), TestOptions.Request("b")],
            _ => "claude-opus-5", 4096, cacheStaticBlock: true);

        var arr = JsonNode.Parse(body)!["requests"]!.AsArray();
        Assert.Equal(2, arr.Count);
        Assert.Equal("a", arr[0]!["custom_id"]!.GetValue<string>());
        Assert.Equal("b", arr[1]!["custom_id"]!.GetValue<string>());
    }

    [Fact]
    public void ParseBatchResults_ReadsJsonl_NotAJsonArray()
    {
        // The endpoint returns JSONL — one object per line. Parsing it as an array yields nothing.
        var jsonl =
            """{"custom_id":"a","result":{"type":"succeeded","message":{"model":"m","stop_reason":"end_turn","content":[{"type":"text","text":"A"}],"usage":{"input_tokens":10,"output_tokens":2}}}}""" + "\n" +
            """{"custom_id":"b","result":{"type":"succeeded","message":{"model":"m","stop_reason":"end_turn","content":[{"type":"text","text":"B"}],"usage":{"input_tokens":20,"output_tokens":4}}}}""";

        var parsed = AnthropicWire.ParseBatchResults(jsonl);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("A", parsed[0].RawOutput);
        Assert.Equal(20, parsed[1].Usage.Input);
    }

    [Theory]
    [InlineData("errored")]
    [InlineData("canceled")]
    [InlineData("expired")]
    public void ParseBatchResults_NonSuccessKindsAreUnavailable_NotExceptions(string kind)
    {
        // A missing read is a no-read day (D24), never something that takes the pipeline down. All three
        // non-success kinds are exercised because handling only 'errored' is the easy mistake.
        var parsed = AnthropicWire.ParseBatchResults(
            """{"custom_id":"a","result":{"type":"KIND"}}""".Replace("KIND", kind, StringComparison.Ordinal));

        Assert.Single(parsed);
        Assert.Equal(AnalysisOutcome.Unavailable, parsed[0].Outcome);
    }

    [Fact]
    public void ParseMessage_RefusalIsDetectedBeforeContentIsRead()
    {
        // A refusal is a SUCCESSFUL HTTP response with empty content. Code that reads content[0]
        // unconditionally throws here — on a 200, which is what makes it easy to miss.
        var msg = JsonNode.Parse(
            """{"model":"claude-opus-5","stop_reason":"refusal","stop_details":{"category":"cyber"},"content":[],"usage":{"input_tokens":7,"output_tokens":0}}""");

        var parsed = AnthropicWire.ParseMessage("a", msg);

        Assert.Equal(AnalysisOutcome.Refused, parsed.Outcome);
        Assert.Equal("refusal:cyber", parsed.Detail);
        Assert.Equal("", parsed.RawOutput);
        Assert.Equal(7, parsed.Usage.Input);
    }

    [Fact]
    public void ParseMessage_ReadsCacheUsageFields()
    {
        var msg = JsonNode.Parse(
            """{"model":"m","stop_reason":"end_turn","content":[{"type":"text","text":"x"}],"usage":{"input_tokens":1,"output_tokens":2,"cache_read_input_tokens":300,"cache_creation_input_tokens":40}}""");

        var parsed = AnthropicWire.ParseMessage("a", msg);

        Assert.Equal(300, parsed.Usage.CacheRead);
        Assert.Equal(40, parsed.Usage.CacheWrite);
    }

    [Fact]
    public void ParseMessage_SkipsNonTextBlocks()
    {
        // Thinking is on by default on the pinned tier and its blocks carry empty text; only text blocks
        // are the answer.
        var msg = JsonNode.Parse(
            """{"model":"m","stop_reason":"end_turn","content":[{"type":"thinking","thinking":""},{"type":"text","text":"answer"}],"usage":{}}""");

        Assert.Equal("answer", AnthropicWire.ParseMessage("a", msg).RawOutput);
    }

    [Fact]
    public void PromptHash_ChangesWithEveryLayer()
    {
        var baseline = AnthropicWire.PromptHash(new PromptLayers("a", "b", "c"));

        Assert.NotEqual(baseline, AnthropicWire.PromptHash(new PromptLayers("A", "b", "c")));
        Assert.NotEqual(baseline, AnthropicWire.PromptHash(new PromptLayers("a", "B", "c")));
        Assert.NotEqual(baseline, AnthropicWire.PromptHash(new PromptLayers("a", "b", "C")));
        Assert.Equal(baseline, AnthropicWire.PromptHash(new PromptLayers("a", "b", "c")));
    }
}
