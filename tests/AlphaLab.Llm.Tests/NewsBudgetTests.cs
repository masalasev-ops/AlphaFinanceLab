using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm.Tests;

/// <summary>The D46 news budget (FR-22, TEST_PLAN §6) — the real token lever, enforced pre-token.</summary>
public class NewsBudgetTests
{
    private static readonly IReadOnlySet<string> Universe =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AAPL", "MSFT" };

    private static readonly IReadOnlySet<string> Macro =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MACRO" };

    private static NewsBudgetOptions Opts(int cap = 25, int chars = 2000) =>
        new() { MaxArticlesPerRead = cap, MaxCharsPerArticle = chars };

    private static NewsArticle Article(
        string title, string? content = null, string[]? symbols = null, string[]? tags = null) =>
        new(title, content ?? "body", "src", symbols ?? ["AAPL.US"], tags ?? []);

    [Fact]
    public void FR22_NewsBudget_CapsAndDedupes()
    {
        // TEST_PLAN §6: 80 raw articles in ⇒ ≤25 admitted, duplicates collapsed by title hash, each
        // ≤2,000 chars — all pre-token.
        var raw = new List<NewsArticle>();
        for (var i = 0; i < 40; i++) raw.Add(Article($"Story {i}", new string('x', 5000)));
        for (var i = 0; i < 40; i++) raw.Add(Article("Story 0", new string('x', 5000)));   // duplicates

        var (admitted, report) = NewsBudget.Apply(raw, Universe, Macro, Opts());

        Assert.Equal(25, admitted.Count);
        Assert.All(admitted, a => Assert.True(a.Article.Content.Length <= 2000));
        Assert.Equal(80, report.Fetched);
        Assert.Equal(40, report.Duplicates);          // "Story 0" was kept in the first loop; all 40 copies collapse
        Assert.Equal(15, report.OverCap);             // 40 distinct - 25 admitted
        Assert.True(report.TruncatedChars > 0);
    }

    [Fact]
    public void Dedupe_RunsBeforeTheCap_SoOneSyndicatedStoryCannotEatTheAllowance()
    {
        // The ordering that matters most. 30 copies of one wire story plus 5 real ones: dedupe-then-cap
        // admits all 6 distinct; cap-then-dedupe would admit 1. Same inputs, wildly different day.
        var raw = new List<NewsArticle>();
        for (var i = 0; i < 30; i++) raw.Add(Article("Wire story"));
        for (var i = 0; i < 5; i++) raw.Add(Article($"Real story {i}"));

        var (admitted, _) = NewsBudget.Apply(raw, Universe, Macro, Opts(cap: 25));

        Assert.Equal(6, admitted.Count);
    }

    [Fact]
    public void TitleHash_IsCaseAndWhitespaceInsensitive()
    {
        // Syndicated copies differ in casing and spacing far more often than in wording; a dedupe that
        // misses those is the one that lets a single story eat the cap.
        Assert.Equal(
            NewsBudget.TitleHash("Fed  Holds   Rates"),
            NewsBudget.TitleHash("  fed holds rates "));
        Assert.NotEqual(NewsBudget.TitleHash("Fed holds"), NewsBudget.TitleHash("Fed cuts"));
    }

    [Fact]
    public void RelevanceFilter_AdmitsUniverseSymbols_AndMacroTags_AndNothingElse()
    {
        var raw = new[]
        {
            Article("in universe", symbols: ["MSFT.US"]),
            Article("macro", symbols: ["XYZ.US"], tags: ["MACRO"]),
            Article("neither", symbols: ["XYZ.US"], tags: ["SPORTS"]),
        };

        var (admitted, report) = NewsBudget.Apply(raw, Universe, Macro, Opts());

        Assert.Equal(2, admitted.Count);
        Assert.Equal(1, report.Irrelevant);
    }

    [Fact]
    public void RelevanceFilter_EmptyUniverse_AdmitsNothingOnTheSymbolArm_FailClosed()
    {
        // Rule 10: an unresolved universe must not silently widen the read to whatever the feed returned.
        var raw = new[] { Article("story", symbols: ["AAPL.US"]) };

        var (admitted, _) = NewsBudget.Apply(raw, new HashSet<string>(), Macro, Opts());

        Assert.Empty(admitted);
    }

    [Fact]
    public void Truncation_RecordsHowMuchWasRemoved_PerArticle()
    {
        // The count is what makes the budget's effect on what the model saw measurable rather than
        // assumed — a read that truncated nothing and one that halved every article look identical
        // without it.
        var raw = new[] { Article("long", new string('x', 3000)), Article("short", "tiny") };

        var (admitted, report) = NewsBudget.Apply(raw, Universe, Macro, Opts(chars: 2000));

        Assert.Equal(1000, admitted[0].TruncatedChars);
        Assert.Equal(0, admitted[1].TruncatedChars);
        Assert.Equal(1, report.Truncated);
        Assert.Equal(1000, report.TruncatedChars);
    }

    [Fact]
    public void EmptyFeed_IsAnEmptyDay_NotAnError()
    {
        var (admitted, report) = NewsBudget.Apply([], Universe, Macro, Opts());
        Assert.Empty(admitted);
        Assert.Equal(NewsBudgetReport.Empty, report);
    }
}

/// <summary>The decorator that makes the budget unbypassable, plus the D24 degradation order.</summary>
public class BudgetedNewsProviderTests
{
    private sealed class RawFeed(params NewsArticle[] articles) : INewsProvider
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<NewsArticle>> GetAdmittedAsync(string asOf, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<NewsArticle>>(articles);
        }
    }

    private sealed class RecordingStore : IAdmittedNewsStore
    {
        public List<AdmittedArticle> Saved { get; } = [];
        public Task SaveAsync(string asOf, IReadOnlyList<AdmittedArticle> admitted, CancellationToken ct = default)
        {
            Saved.AddRange(admitted);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Decorator_AppliesTheBudget_AndPersistsOnlyWhatSurvived()
    {
        var raw = new RawFeed(
            new NewsArticle("A", new string('x', 4000), "s", ["AAPL.US"], []),
            new NewsArticle("A", "dup", "s", ["AAPL.US"], []),
            new NewsArticle("Z", "irrelevant", "s", ["XYZ.US"], []));
        var store = new RecordingStore();
        var llm = new LlmOptions { NewsBudget = new NewsBudgetOptions { MaxArticlesPerRead = 25, MaxCharsPerArticle = 2000 } };

        var provider = new BudgetedNewsProvider(
            raw, store, llm,
            () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AAPL" });

        var admitted = await provider.GetAdmittedAsync("2026-08-03");

        // One article survives: the duplicate collapses, the irrelevant one is filtered.
        Assert.Single(admitted);
        Assert.Equal(2000, admitted[0].Content.Length);

        // news_items records what the budget ADMITTED, not what the feed returned.
        Assert.Single(store.Saved);
        Assert.Equal(2000, store.Saved[0].TruncatedChars);

        Assert.Equal(3, provider.LastReport.Fetched);
        Assert.Equal(1, provider.LastReport.Irrelevant);
        Assert.Equal(1, provider.LastReport.Duplicates);
    }

    [Fact]
    public void FR22_Budget_DegradesInOrder()
    {
        // D24's order is HELD NAMES FIRST, then whatever is cached, then a neutral fallback — an ordered
        // degradation, never a blackout. Read through the Resolved accessor: the raw property is empty by
        // default because a configured collection APPENDS to a populated one (finding 301), which here
        // would leave a removed entry not just present but still AHEAD in priority.
        var llm = new LlmOptions();
        Assert.Equal(["held_positions", "cached", "neutral_fallback"], llm.ResolvedDegradationOrder);

        llm.DegradationOrder = ["cached"];
        Assert.Equal(["cached"], llm.ResolvedDegradationOrder);
        Assert.DoesNotContain("held_positions", llm.ResolvedDegradationOrder);
    }
}
