using System.Text.Json;
using AlphaLab.Data.Entities;
using AlphaLab.Core.Llm;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Services;

/// <summary>
/// <see cref="IAdmittedNewsStore"/> over <c>news_items</c> (D46).
///
/// **POST-BUDGET only.** Nothing the relevance filter, the dedupe, the cap or the truncation removed is
/// persisted, so the table answers "what did the model actually see that day?" rather than "what did the
/// feed return?" — the second question is answerable from the raw cache if it is ever asked.
///
/// Idempotent by construction: the day's existing hashes are read first and skipped, so a re-run adds
/// nothing. The <c>ux_news_items_as_of_title</c> unique index is the backstop, not the mechanism — if the
/// two ever disagree, the index wins and the re-run fails loudly rather than silently duplicating.
/// </summary>
public sealed class AdmittedNewsStore(AlphaLabDbContext db) : IAdmittedNewsStore
{
    public async Task SaveAsync(
        string asOf, IReadOnlyList<AdmittedArticle> admitted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        if (admitted.Count == 0) return;

        var existing = await db.NewsItems
            .Where(n => n.AsOf == asOf)
            .Select(n => n.TitleHash)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);

        var added = false;
        foreach (var a in admitted)
        {
            if (!seen.Add(a.TitleHash)) continue;

            db.NewsItems.Add(new NewsItemRow
            {
                AsOf = asOf,
                TitleHash = a.TitleHash,
                Title = a.Article.Title,
                Source = a.Article.Source,
                SymbolsJson = JsonSerializer.Serialize(a.Article.Symbols),
                TruncatedChars = a.TruncatedChars,
            });
            added = true;
        }

        if (added) await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
