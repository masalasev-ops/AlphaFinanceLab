using AlphaLab.Core.Llm;
using AlphaLab.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Services;

/// <summary>
/// <see cref="IAnalysisCache"/> over <c>analysis_cache</c> (FR-21).
///
/// A hit spends nothing — <c>FR21_CacheHit_CostsZero</c>. The key is (prompt_hash, model, as_of), so a
/// re-pinned model correctly MISSES rather than serving an answer the current tier never produced.
///
/// Writes are idempotent: a second put for the same key is a no-op rather than a duplicate-key throw,
/// because a re-run of a day must be able to replay without either failing or double-charging.
/// </summary>
public sealed class AnalysisCacheStore(AlphaLabDbContext db) : IAnalysisCache
{
    public async Task<CachedAnalysis?> TryGetAsync(
        string promptHash, string model, string asOf, CancellationToken ct = default)
    {
        var row = await db.AnalysisCache
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.PromptHash == promptHash && r.Model == model && r.AsOf == asOf, ct)
            .ConfigureAwait(false);

        if (row is null) return null;

        // The ORIGINAL usage is carried for the record; the caller reports TokenUsage.Zero for the hit
        // itself, because the tokens were paid once on the day that paid them.
        return new CachedAnalysis(
            row.OutputJson,
            new TokenUsage(
                row.InputTokens ?? 0, row.OutputTokens ?? 0, 0, 0, (decimal)(row.CostUsd ?? 0d)));
    }

    public async Task PutAsync(
        string promptHash, string model, string asOf, AnalysisTask task,
        string rawOutput, TokenUsage usage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var exists = await db.AnalysisCache
            .AnyAsync(r => r.PromptHash == promptHash && r.Model == model && r.AsOf == asOf, ct)
            .ConfigureAwait(false);
        if (exists) return;

        db.AnalysisCache.Add(new AnalysisCacheRow
        {
            PromptHash = promptHash,
            Model = model,
            AsOf = asOf,
            Task = task.Wire(),
            OutputJson = rawOutput,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            // decimal → double at the storage boundary only: SCHEMA declares this column REAL because D69
            // governs LEDGER money, and an operational spend metric is not that. The arithmetic upstream
            // stays decimal so a day's calls do not accumulate float error.
            CostUsd = (double)usage.CostUsd,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// <see cref="ILlmBudgetLedger"/> over <c>llm_budget_log</c> (D24).
///
/// One row per day, accumulated in place: the ceiling is a DAILY total, so a per-call row would make
/// every pre-flight check a scan. <c>degraded</c> is sticky once set — a day on which anything was
/// refused stays marked, because the question it answers ("did we see everything that day?") cannot be
/// un-answered by a later successful call.
/// </summary>
public sealed class LlmBudgetLedger(AlphaLabDbContext db) : ILlmBudgetLedger
{
    public async Task<BudgetState> GetAsync(string asOf, CancellationToken ct = default)
    {
        var row = await db.LlmBudgetLog.AsNoTracking()
            .FirstOrDefaultAsync(r => r.AsOf == asOf, ct).ConfigureAwait(false);

        return row is null
            ? BudgetState.Empty
            : new BudgetState(row.Calls, row.Tokens, (decimal)row.CostUsd);
    }

    public async Task RecordAsync(
        string asOf, int calls, TokenUsage usage, bool degraded, string? note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var row = await db.LlmBudgetLog.FirstOrDefaultAsync(r => r.AsOf == asOf, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new LlmBudgetLogRow { AsOf = asOf };
            db.LlmBudgetLog.Add(row);
        }

        row.Calls += calls;
        row.Tokens += usage.TotalTokens;
        row.CostUsd += (double)usage.CostUsd;
        if (degraded) row.Degraded = 1;
        if (note is { Length: > 0 })
        {
            row.Note = row.Note is { Length: > 0 } ? $"{row.Note}; {note}" : note;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
