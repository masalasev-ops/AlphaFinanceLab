using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm;

/// <summary>
/// The D46 budget as a **decorator** over the raw feed (FR-22).
///
/// **Why a decorator and not a method on the fetcher.** The budget must be impossible to bypass. If the
/// enforcement lived inside `EodhdNewsProvider`, any future caller that reached for the raw fetch would
/// get an unbudgeted read and nothing would fail — the cost would surface on the bill, not in a test.
/// As a decorator over the Core interface, the only thing composition can hand out is the budgeted one,
/// and the raw fetch becomes an implementation detail nothing else references. Same shape and same reason
/// as <see cref="BudgetedAnalysisProvider"/>.
///
/// Everything here runs **before any token is spent** (rule 13). Persisting the survivors is part of the
/// same step, so <c>news_items</c> records what the budget ADMITTED rather than what the feed returned.
/// </summary>
public sealed class BudgetedNewsProvider(
    INewsProvider inner,
    IAdmittedNewsStore store,
    LlmOptions llm,
    Func<IReadOnlySet<string>> universeSymbols,
    IReadOnlySet<string>? macroTags = null) : INewsProvider
{
    /// <summary>Macro tags that admit an article with no universe symbol — the market-level read the
    /// regime brief is built on (ScopeLevel 1 is one market-level read per day).</summary>
    public static readonly IReadOnlySet<string> DefaultMacroTags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MACRO", "ECONOMY", "FEDERAL RESERVE", "INFLATION", "INTEREST RATES",
            "EMPLOYMENT", "GDP", "MARKETS", "MONETARY POLICY", "RECESSION",
        };

    private readonly IReadOnlySet<string> _macroTags = macroTags ?? DefaultMacroTags;

    /// <summary>The last day's budget arithmetic, for the caller to log. Not persisted as a metric —
    /// it is operational provenance, and rule 32 keeps it away from anything that judges anything.</summary>
    public NewsBudgetReport LastReport { get; private set; } = NewsBudgetReport.Empty;

    public async Task<IReadOnlyList<NewsArticle>> GetAdmittedAsync(string asOf, CancellationToken ct = default)
    {
        var fetched = await inner.GetAdmittedAsync(asOf, ct).ConfigureAwait(false);

        var (admitted, report) = NewsBudget.Apply(
            fetched, universeSymbols(), _macroTags, llm.NewsBudget);
        LastReport = report;

        await store.SaveAsync(asOf, admitted, ct).ConfigureAwait(false);

        return [.. admitted.Select(a => a.Article)];
    }
}
