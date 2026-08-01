using AlphaLab.Core.Config;

namespace AlphaLab.Core.Tests;

/// <summary>
/// **finding 328** — the Anthropic API resolves a pinned ALIAS to a dated SNAPSHOT and reports the
/// snapshot, so `Llm.Pricing` must be keyed by the alias and resolved by prefix.
///
/// **This was found by the live smoke test and could not have been found any other way.** Every mocked
/// test echoes back the model string it was asked for, so the requested and reported model are equal by
/// construction in the entire mocked suite. The real batch returned `claude-haiku-4-5-20251001` for a
/// request pinned to `claude-haiku-4-5`, `PricingFor` threw on the exact-key lookup, and the whole
/// forward LLM path was dead on real traffic — from a defect no amount of documentation review would have
/// surfaced, since it is about what the API RETURNS, not what it accepts.
/// </summary>
public class AliasSnapshotPricingTests
{
    private static LlmOptions Priced(params string[] keys)
    {
        var llm = new LlmOptions();
        foreach (var k in keys)
        {
            llm.Pricing[k] = new ModelPriceOptions { InputPerMTok = 1m, OutputPerMTok = 5m };
        }
        return llm;
    }

    [Fact]
    public void Finding328_ADatedSnapshot_IsPricedByItsAlias()
    {
        // The literal observed case: pinned claude-haiku-4-5, served claude-haiku-4-5-20251001.
        var llm = Priced("claude-haiku-4-5");

        var price = llm.PricingFor("claude-haiku-4-5-20251001");

        Assert.Equal(1m, price.InputPerMTok);
    }

    [Fact]
    public void AnExactKey_StillWinsOverAnyPrefix()
    {
        // Exact-first matters: an operator who prices ONE snapshot differently (a promotional rate, a
        // corrected published figure) must get that snapshot's rate, not the family's.
        var llm = Priced("claude-haiku-4-5");
        llm.Pricing["claude-haiku-4-5-20251001"] = new ModelPriceOptions { InputPerMTok = 99m, OutputPerMTok = 99m };

        Assert.Equal(99m, llm.PricingFor("claude-haiku-4-5-20251001").InputPerMTok);
        Assert.Equal(1m, llm.PricingFor("claude-haiku-4-5-19990101").InputPerMTok);
    }

    [Fact]
    public void TheLONGESTPrefixWins_SoANestedFamilyIsNotMispriced()
    {
        // The reason the rule is longest-match and not first-match. With both configured, an Opus 4.8
        // snapshot must price as 4.8 — a shortest-match rule would silently charge it at the broader
        // family's rate, which is the failure mode that is invisible in every downstream number.
        var llm = new LlmOptions();
        llm.Pricing["claude-opus-4"] = new ModelPriceOptions { InputPerMTok = 1m, OutputPerMTok = 1m };
        llm.Pricing["claude-opus-4-8"] = new ModelPriceOptions { InputPerMTok = 5m, OutputPerMTok = 25m };

        Assert.Equal(5m, llm.PricingFor("claude-opus-4-8-20260210").InputPerMTok);
        Assert.Equal(1m, llm.PricingFor("claude-opus-4-1-20250101").InputPerMTok);
    }

    [Fact]
    public void AnUnknownFamily_STILL_FAILS_CLOSED()
    {
        // The property the fix must not lose (rule 10 / D24). The relaxation is from "exact key" to
        // "known family" — never from "known" to "anything". A zero cost is indistinguishable from a free
        // cache hit in llm_budget_log, so an unpriced model must throw rather than cost nothing.
        var llm = Priced("claude-haiku-4-5", "claude-opus-5");

        var ex = Assert.Throws<InvalidOperationException>(() => llm.PricingFor("gpt-4o-2026-01-01"));
        Assert.Contains("no configured rate is a prefix of it", ex.Message, StringComparison.Ordinal);

        // A near-miss is still a miss: a shorter string is not a snapshot of a longer key.
        Assert.Throws<InvalidOperationException>(() => llm.PricingFor("claude-haiku-4"));
    }

    [Fact]
    public void ThePinnedTiersPriceEveryDatedSnapshotOfThemselves()
    {
        // The committed v1.9.60 pins, against the snapshot form each actually returns. This is the
        // assertion that would have failed before the fix and that keeps the forward path alive.
        var llm = Priced("claude-opus-5", "claude-haiku-4-5");

        foreach (var served in new[]
        {
            "claude-opus-5", "claude-opus-5-20260115",
            "claude-haiku-4-5", "claude-haiku-4-5-20251001",
        })
        {
            Assert.Equal(1m, llm.PricingFor(served).InputPerMTok);
        }
    }
}
