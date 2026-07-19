using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Funnel;

/// <summary>The per-day inputs the funnel cannot derive for itself. Assembled by the caller (the
/// D53 pipeline at 2.10) from Data services, so the runner stays pure.</summary>
public sealed record FunnelInputs
{
    /// <summary>The as-of index roster, from IIndexMembershipRead (Data).</summary>
    public required IReadOnlyList<SecurityId> IndexMembers { get; init; }

    /// <summary>The account's book, POST corporate actions (D53 applies actions before the funnel).</summary>
    public required IReadOnlyList<Position> Held { get; init; }

    public required decimal Equity { get; init; }

    /// <summary>The account's available CASH — post corporate actions, post the day's T+1 fills — the
    /// only money a new open can spend (D84). New opens are sized against this, never total equity; a
    /// whole-book rebalance re-weights against equity instead. Computed once by the pipeline.</summary>
    public required decimal Cash { get; init; }

    /// <summary>The next trading session — where these orders will fill. From the calendar (D54);
    /// never asOf+1 calendar day, which would fabricate a session on a Friday.</summary>
    public required DateOnly FillOn { get; init; }

    /// <summary>Sessions since the account's inception, from the calendar. Drives the rebalance
    /// cadence.</summary>
    public required int SessionsSinceInception { get; init; }
}

/// <summary>What one strategy-day produced: the snapshot (which carries the orders) and nothing
/// else. The caller persists it; this type never touches a DB.</summary>
public sealed record FunnelOutcome(DecisionSnapshot Snapshot)
{
    public IReadOnlyList<PlannedOrder> Orders => Snapshot.Stage6Orders;
}

/// <summary>
/// Runs the six-stage funnel (MASTER §6) for ONE strategy on ONE day. PURE — no DB, no clock, no
/// network. Everything it needs arrives as <see cref="FunnelInputs"/> plus an
/// <see cref="IFeatureView"/>, which is the only thing that touches data and is itself
/// watermark-bounded.
///
/// The stages, and who owns each:
///   1 Eligibility  — SHARED (same pool for every strategy, so differences are about the strategy)
///   2 Scoring      — PER-STRATEGY (the IModel; this runner just calls it)
///   3 Selection    — SHARED (the zero-score invariant lives here)
///   4 Portfolio    — SHARED mechanics, PER-STRATEGY ExitPolicy
///   5 Size &amp; safety — SHARED (equal only in Phase 2; FR-11 partial)
///   6 Orders       — SHARED (decide at close T, fill at open T+1)
///
/// DETERMINISM (F-DET) is a contract, not an accident: given the same inputs, watermark, and seed,
/// two runs produce a byte-identical snapshot. Every stage orders its output by security_id and
/// breaks score ties by id for exactly this reason. It is what makes Phase-4 replay reproducible
/// and what lets a decision be re-read rather than re-derived.
/// </summary>
public static class FunnelRunner
{
    public static async Task<FunnelOutcome> RunAsync(
        IModel model,
        IFeatureView features,
        FunnelInputs inputs,
        GuardrailsOptions guardrails,
        SizingOptions sizing,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(guardrails);
        ArgumentNullException.ThrowIfNull(sizing);

        var asOf = features.AsOf;
        var notes = new List<StageNote>();

        // ---- Stage 1: eligibility (shared) ----
        var eligibility = Eligibility.Resolve(inputs.IndexMembers, asOf, features);
        notes.AddRange(StageNote.From(1, eligibility.Excluded));

        // ---- Stage 2: scoring (per-strategy) ----
        var scores = await model.ScoreUniverseAsync(eligibility.Eligible, asOf, features, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Model '{model.Id}' returned null from ScoreUniverseAsync. Catalog §2 says omit a security that " +
                "lacks data — an empty map is the way to say 'nothing scored', never null.");

        // A model may only score what it was handed. Scoring outside the eligible pool would mean a
        // strategy trading a name Stage 1 ruled out (unpriced, or not in the index that day) — a
        // point-in-time violation wearing a plausible face, so it fails loudly rather than being
        // filtered away in silence.
        var eligibleSet = eligibility.Eligible.ToHashSet();
        var strays = scores.Keys.Where(k => !eligibleSet.Contains(k)).OrderBy(k => k.Value).ToList();
        if (strays.Count > 0)
        {
            throw new InvalidOperationException(
                $"Model '{model.Id}' scored {strays.Count} security(ies) outside the eligible pool it was given " +
                $"(e.g. {string.Join(", ", strays.Take(5))}). Stage 2 may only score what Stage 1 handed it.");
        }

        // ---- Stage 3: selection (shared) — the zero-score invariant ----
        var selection = Selection.Select(scores, model.Config.Selection, guardrails);
        notes.AddRange(StageNote.From(3, selection.Excluded));

        // ---- Stage 4: portfolio (shared mechanics, per-strategy ExitPolicy) ----
        var wishList = selection.WishList.ToHashSet();
        var context = new ExitContext
        {
            AsOf = asOf,
            Ranks = RanksOf(scores),
            WishList = wishList,
            SessionsSinceInception = inputs.SessionsSinceInception,
        };

        var plan = PortfolioPlanner.Plan(inputs.Held, model.Exits, context);
        notes.AddRange(StageNote.From(4, plan.Notes));

        // ---- Stage 5: size & safety (shared) ----
        // The sizing MODE is the strategy's own declared choice (Config.Sizing) — a dimension
        // candidates differ on, exactly like the SelectionRule. SizingOptions supplies only the
        // system knob the mode is applied under (PositionCapPct; and the D42/Kelly params Phase 6
        // reads). So the mode comes from the model, the cap from config.
        //
        // D84 CASH CONSTRAINT: the spendable budget depends on what Stage 4 decided to size. New opens
        // (OpensOnly) can only spend the account's CASH — there are no trims to fund them, and selling a
        // held name to fund a buy is forbidden (rule 7). A whole-book rebalance (ScheduledRebalance/D68)
        // re-weights the entire book, so its trims self-fund and the budget is EQUITY (targets sum ≤
        // equity and held + cash = equity, so cash stays ≥ 0). Sizing scales the opens to fit, so an
        // account never spends cash it does not hold.
        var spendable = plan.Scope == RebalanceScope.WholeBook ? inputs.Equity : inputs.Cash;
        var sized = Sizing.Size(plan.ToSize, inputs.Equity, spendable, model.Config.Sizing, sizing);
        notes.AddRange(StageNote.From(5, sized.Excluded));

        // ---- Stage 6: orders (shared) — decide at close T, fill at open T+1 ----
        var stage6Notes = new List<FunnelNote>();
        var orders = OrderBuilder.Build(
            plan, sized, inputs.Held, asOf, inputs.FillOn,
            id => features.RawClose(id, asOf),
            stage6Notes);
        notes.AddRange(StageNote.From(6, stage6Notes));

        var snapshot = new DecisionSnapshot
        {
            StrategyId = model.Id,
            AsOf = asOf.ToString("yyyy-MM-dd"),
            Watermark = features.Watermark,
            Stage1Eligible = eligibility.Eligible,
            Stage2Scores = ScoredNames(scores),
            Stage3WishList = selection.WishList,
            Stage4 = new Stage4Snapshot(plan.Opens, plan.Holds, plan.Closes, plan.Scope),
            Stage5Targets = sized.Targets,
            Stage5UninvestedCash = sized.UninvestedCash,
            Stage6Orders = orders,
            Notes = notes,
        };

        return new FunnelOutcome(snapshot);
    }

    /// <summary>
    /// Cross-sectional rank over the scored universe: 1 = highest score, ties broken by security_id.
    ///
    /// The tiebreak is the same one Selection uses, and it must be — RankBuffer's exit compares
    /// against a rank that Selection's entry produced, so two different tie orders would let a name
    /// be simultaneously inside the top N and past the exit buffer.
    ///
    /// Zero-scored names ARE ranked (at the bottom). They are never selectable, but they are still
    /// part of the cross-section a held name's rank is measured against — which is exactly how a
    /// held name that collapses to zero ends up past its exit buffer and gets closed.
    /// </summary>
    private static Dictionary<SecurityId, int> RanksOf(IReadOnlyDictionary<SecurityId, double> scores) =>
        scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.Value)
            .Select((kv, i) => (kv.Key, Rank: i + 1))
            .ToDictionary(x => x.Key, x => x.Rank);

    private static List<ScoredName> ScoredNames(IReadOnlyDictionary<SecurityId, double> scores)
    {
        var ranks = RanksOf(scores);
        return scores
            .OrderBy(kv => ranks[kv.Key])
            .Select(kv => new ScoredName(kv.Key, kv.Value, ranks[kv.Key]))
            .ToList();
    }
}
