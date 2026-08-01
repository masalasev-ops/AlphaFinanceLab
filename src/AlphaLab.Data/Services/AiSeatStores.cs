using System.Globalization;
using AlphaLab.Core.Llm;
using AlphaLab.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Services;

/// <summary>Persists a built <see cref="ContextPack"/> (D80).</summary>
public interface IContextPackStore
{
    /// <summary>Persist a pack, or return the hash already stored for the same
    /// (seat, strategy, as_of, recipe_version). Append-only: an existing row is NEVER overwritten,
    /// because the pack is the record of what the model saw and a second build cannot revise history.</summary>
    Task<string> SaveAsync(ContextPack pack, CancellationToken ct = default);

    Task<ContextPack?> TryGetAsync(
        string seat, string? strategyId, string asOf, string recipeVersion, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IContextPackStore"/> over <c>ai_context_packs</c>.
///
/// **Append-only, and the idempotency is deliberate rather than defensive.** `FX-PackWatermark` asserts a
/// pack built at watermark W is byte-identical however often it is built, so a second save of the same key
/// is either identical (nothing to do) or a defect. When the hashes differ the store **throws**: silently
/// keeping the first would hide a recipe that stopped being deterministic, which is precisely the property
/// the pack exists to guarantee.
/// </summary>
public sealed class ContextPackStore(AlphaLabDbContext db) : IContextPackStore
{
    public async Task<string> SaveAsync(ContextPack pack, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pack);

        var existing = await db.AiContextPacks.FirstOrDefaultAsync(
            r => r.Seat == pack.Seat && r.StrategyId == pack.StrategyId
                 && r.AsOf == pack.AsOf && r.RecipeVersion == pack.RecipeVersion, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!string.Equals(existing.PackHash, pack.PackHash, StringComparison.Ordinal))
            {
                throw new PackViolationException(
                    $"A pack already exists for (seat={pack.Seat}, strategy={pack.StrategyId}, " +
                    $"as_of={pack.AsOf}, recipe={pack.RecipeVersion}) with hash {existing.PackHash}, but " +
                    $"rebuilding produced {pack.PackHash}. A pack at a fixed watermark must be " +
                    "byte-identical on every build (FX-PackWatermark) — a differing rebuild means the " +
                    "recipe is not deterministic, and keeping the first row would hide that.");
            }
            return existing.PackHash;
        }

        db.AiContextPacks.Add(new AiContextPackRow
        {
            Seat = pack.Seat,
            StrategyId = pack.StrategyId,
            AsOf = pack.AsOf,
            Watermark = pack.Watermark,
            RecipeVersion = pack.RecipeVersion,
            PackJson = pack.PackJson,
            PackHash = pack.PackHash,
            TokenEstimate = pack.TokenEstimate,
            CreatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return pack.PackHash;
    }

    public async Task<ContextPack?> TryGetAsync(
        string seat, string? strategyId, string asOf, string recipeVersion, CancellationToken ct = default)
    {
        var row = await db.AiContextPacks.AsNoTracking().FirstOrDefaultAsync(
            r => r.Seat == seat && r.StrategyId == strategyId
                 && r.AsOf == asOf && r.RecipeVersion == recipeVersion, ct)
            .ConfigureAwait(false);

        return row is null
            ? null
            // Fields are not rehydrated: the STORED BYTES are the record (D104 artefact (a)), and
            // reconstructing a field list from them would invent a parse the original never went through.
            : new ContextPack(
                row.Seat, row.StrategyId, row.AsOf, row.Watermark, row.RecipeVersion,
                [], row.PackJson, row.PackHash, row.TokenEstimate);
    }
}

/// <summary>
/// <see cref="IAiDecisionStore"/> over <c>ai_decisions</c> (D81 rule 1, D104, D105).
///
/// **Append-only, and that is the reproducibility mechanism rather than a storage preference.** If a
/// second call could overwrite a stored decision, "the persisted output is the decision" would be true
/// only until something called again — and `reproduce-day` would be replaying whichever call happened
/// last, not the one the day actually traded on.
/// </summary>
public sealed class AiDecisionStore(AlphaLabDbContext db) : IAiDecisionStore
{
    public async Task<AiDecisionRecord> PersistAsync(AiDecisionRecord decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var existing = await Find(decision.StrategyId, decision.AsOf, decision.PromptVersion, ct)
            .ConfigureAwait(false);
        if (existing is not null) return Map(existing);

        db.AiDecisions.Add(new AiDecisionRow
        {
            StrategyId = decision.StrategyId,
            AsOf = decision.AsOf,
            PackHash = decision.PackHash,
            PromptVersion = decision.PromptVersion,
            ModelVersion = decision.ModelVersion,
            OutputJson = decision.RawOutput,
            AppliedJson = decision.AppliedJson,
            SamplingJson = decision.SamplingJson,
            TokensIn = decision.Usage.InputTokens,
            TokensOut = decision.Usage.OutputTokens,
            // decimal -> TEXT (D69): this row is part of a decision record a ledger claim can rest on,
            // which is the line SCHEMA draws between it and analysis_cache's REAL cost column.
            CostUsd = decision.Usage.CostUsd.ToString(CultureInfo.InvariantCulture),
            CreatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return decision;
    }

    public async Task<AiDecisionRecord?> TryGetAsync(
        string strategyId, string asOf, string promptVersion, CancellationToken ct = default)
    {
        var row = await Find(strategyId, asOf, promptVersion, ct).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task RecordAppliedAsync(
        string strategyId, string asOf, string promptVersion, string appliedJson, CancellationToken ct = default)
    {
        var row = await db.AiDecisions.FirstOrDefaultAsync(
            r => r.StrategyId == strategyId && r.AsOf == asOf && r.PromptVersion == promptVersion, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            throw new InvalidOperationException(
                $"No ai_decisions row for ({strategyId}, {asOf}, {promptVersion}) to record an application " +
                "against. Artefact (c) describes what the funnel did with a decision — recording one for a " +
                "decision that was never persisted would be recording an application of nothing.");
        }

        // Append-only in spirit: (c) is written ONCE, when the funnel acts. A second application of the
        // same decision would mean the decision was consumed twice, which is itself the defect.
        if (row.AppliedJson is { Length: > 0 }) return;

        row.AppliedJson = appliedJson;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private Task<AiDecisionRow?> Find(string strategyId, string asOf, string promptVersion, CancellationToken ct) =>
        db.AiDecisions.FirstOrDefaultAsync(
            r => r.StrategyId == strategyId && r.AsOf == asOf && r.PromptVersion == promptVersion, ct);

    private static AiDecisionRecord Map(AiDecisionRow r) => new(
        r.StrategyId, r.AsOf, r.PackHash, r.PromptVersion, r.ModelVersion, r.OutputJson,
        new TokenUsage(r.TokensIn, r.TokensOut, 0, 0, decimal.Parse(r.CostUsd, CultureInfo.InvariantCulture)),
        r.AppliedJson, r.SamplingJson);
}
