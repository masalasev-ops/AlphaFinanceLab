using System.Security.Cryptography;
using System.Text;
using AlphaLab.Core.Config;
using AlphaLab.Core.Llm;

namespace AlphaLab.Llm;

/// <summary>What the D46 budget did to a day's raw feed. Persisted alongside the admitted articles so the
/// budget's effect on what the model saw is MEASURABLE rather than assumed — a read that admitted 25 of
/// 25 and one that admitted 25 of 800 are very different days, and only the counts distinguish them.</summary>
/// <param name="Fetched">Articles the feed returned.</param>
/// <param name="Irrelevant">Dropped by the relevance filter (no universe symbol, no macro tag).</param>
/// <param name="Duplicates">Collapsed by title hash.</param>
/// <param name="OverCap">Dropped by the article cap AFTER filtering and dedupe.</param>
/// <param name="Truncated">Admitted articles whose body was shortened.</param>
/// <param name="TruncatedChars">Total characters removed by truncation.</param>
public sealed record NewsBudgetReport(
    int Fetched, int Irrelevant, int Duplicates, int OverCap, int Truncated, int TruncatedChars)
{
    public static readonly NewsBudgetReport Empty = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The D46 news budget — **the real token lever** (MASTER §7: the sink was never the call count, it was
/// the admitted text).
///
/// Pure and static, so the whole rail is unit-testable with no feed and no model. The ORDER of the four
/// steps is the contract and is not interchangeable:
///
/// <list type="number">
/// <item><b>Relevance filter</b> — universe symbols + macro tags. First, because filtering is free and
/// everything after it is cheaper on a smaller set.</item>
/// <item><b>Title-hash dedupe</b> — before the cap, so the cap counts DISTINCT articles. Deduping after
/// the cap would let a wire story syndicated twenty times consume the entire day's allowance.</item>
/// <item><b>Article cap</b> — after dedupe, for that reason.</item>
/// <item><b>Per-article truncation</b> — last, because it only affects admitted articles and truncating
/// something that is about to be dropped is wasted work.</item>
/// </list>
///
/// Every step runs **before any token is spent** (rule 13). That is the difference between a budget and a
/// bill.
/// </summary>
public static class NewsBudget
{
    /// <summary>Stable dedupe key: SHA-256 of the case- and whitespace-normalised title. Normalised
    /// because syndicated copies differ in casing and spacing far more often than in wording, and a
    /// dedupe that misses those is the one that lets a single story eat the cap.</summary>
    public static string TitleHash(string title)
    {
        var normalised = string.Join(' ', (title ?? "").ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }

    /// <summary>Relevant if the article names a universe symbol or carries a permitted macro tag.
    /// An EMPTY universe admits nothing on the symbol arm — fail closed (rule 10): an unresolved universe
    /// must not silently widen the read to everything the feed happened to return.</summary>
    public static bool IsRelevant(
        NewsArticle article, IReadOnlySet<string> universeSymbols, IReadOnlySet<string> macroTags)
    {
        ArgumentNullException.ThrowIfNull(article);
        foreach (var s in article.Symbols)
        {
            // EODHD returns symbols suffixed (AAPL.US); the universe holds bare tickers.
            var bare = s.Split('.')[0];
            if (universeSymbols.Contains(bare)) return true;
        }
        foreach (var t in article.Tags)
        {
            if (macroTags.Contains(t)) return true;
        }
        return false;
    }

    /// <summary>Apply the whole budget to a day's raw feed.</summary>
    public static (IReadOnlyList<AdmittedArticle> Admitted, NewsBudgetReport Report) Apply(
        IReadOnlyList<NewsArticle> fetched,
        IReadOnlySet<string> universeSymbols,
        IReadOnlySet<string> macroTags,
        NewsBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        ArgumentNullException.ThrowIfNull(options);

        var irrelevant = 0;
        var duplicates = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<(NewsArticle Article, string Hash)>();

        foreach (var a in fetched)
        {
            if (!IsRelevant(a, universeSymbols, macroTags)) { irrelevant++; continue; }

            var hash = TitleHash(a.Title);
            if (!seen.Add(hash)) { duplicates++; continue; }

            kept.Add((a, hash));
        }

        var cap = Math.Max(0, options.MaxArticlesPerRead);
        var overCap = Math.Max(0, kept.Count - cap);
        var capped = kept.Take(cap).ToList();

        var admitted = new List<AdmittedArticle>(capped.Count);
        var truncated = 0;
        var truncatedChars = 0;
        var maxChars = Math.Max(0, options.MaxCharsPerArticle);

        foreach (var (article, hash) in capped)
        {
            var body = article.Content ?? "";
            var removed = 0;
            if (maxChars > 0 && body.Length > maxChars)
            {
                removed = body.Length - maxChars;
                body = body[..maxChars];
                truncated++;
                truncatedChars += removed;
            }
            admitted.Add(new AdmittedArticle(article with { Content = body }, hash, removed));
        }

        return (admitted, new NewsBudgetReport(
            fetched.Count, irrelevant, duplicates, overCap, truncated, truncatedChars));
    }
}
