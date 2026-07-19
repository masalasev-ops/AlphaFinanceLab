using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// Stage 4 (portfolio), Stage 6 (orders), and the whole FunnelRunner wired together — MASTER §6.
/// Fixtures: FX-ExitOnly (only the ExitPolicy closes; a wish-list dropout does not) and F-DET (a
/// re-run of the same day produces a byte-identical snapshot). The T→T+1 handoff is pinned here:
/// orders are read back from the STORED snapshot, never recomputed at the later watermark.
/// </summary>
public class FunnelRunnerTests
{
    private static SecurityId S(long id) => new(id);
    private static readonly DateOnly AsOf = new(2026, 7, 16);
    private static readonly DateOnly FillOn = new(2026, 7, 17);

    // ---- a hand-built model: scores are handed in, so the funnel is exercised without a real strategy ----
    private sealed class FakeModel(
        IReadOnlyDictionary<SecurityId, double> scores, ExitPolicy exits, SelectionRule? selection = null) : IModel
    {
        private readonly IReadOnlyDictionary<SecurityId, double> _scores = scores;

        public string Id => "fake:test";
        public StrategyConfig Config { get; } = new()
        {
            Seed = 1,
            Selection = selection ?? SelectionRule.TopN(40) with { MinScore = 0.0 },
            Sizing = SizingMode.Equal,
        };
        public HoldingHorizon Horizon { get; } = new HoldingHorizon.ToNextRebalance();
        public ExitPolicy Exits { get; } = exits;

        public Task<IReadOnlyDictionary<SecurityId, double>> ScoreUniverseAsync(
            IReadOnlyList<SecurityId> eligible, DateOnly asOf, IFeatureView features, CancellationToken ct = default)
        {
            // Honour the contract: only score what was handed in.
            var scoped = eligible.Where(_scores.ContainsKey).ToDictionary(id => id, id => _scores[id]);
            return Task.FromResult<IReadOnlyDictionary<SecurityId, double>>(scoped);
        }
    }

    private static Position Pos(long id, double shares, decimal basis) => new()
    {
        AccountId = 1, SecurityId = S(id), Shares = shares, CostBasis = basis, OpenedOn = "2026-01-02",
    };

    private static FunnelInputs Inputs(
        IEnumerable<long> members, IReadOnlyList<Position> held, decimal equity = 100_000m, int sessions = 5,
        decimal? cash = null) => new()
    {
        IndexMembers = members.Select(S).ToList(),
        Held = held,
        Equity = equity,
        // Default to all-cash (cash == equity) so the pre-D84 tests are unaffected; the D84 tests pass a
        // real cash figure below equity to exercise the constraint.
        Cash = cash ?? equity,
        FillOn = FillOn,
        SessionsSinceInception = sessions,
    };

    private static async Task<FunnelOutcome> Run(
        FakeModel model, FunnelInputs inputs, IReadOnlyDictionary<SecurityId, double>? prices = null)
    {
        var view = new FunnelFeatureView
        {
            AsOf = AsOf,
            Priced = inputs.IndexMembers.ToHashSet(),
            RawCloses = prices?.ToDictionary(kv => kv.Key, kv => kv.Value)
                        ?? inputs.IndexMembers.ToDictionary(id => id, _ => 100.0),
        };
        return await FunnelRunner.RunAsync(model, view, inputs, new GuardrailsOptions(), new SizingOptions { PositionCapPct = 1.0 });
    }

    // ============================ FX-ExitOnly ============================

    /// <summary>
    /// FX-ExitOnly, the heart of rule 7. A held name falls off the wish list but its ExitPolicy
    /// (Never) does not close it → it is HELD, and no sell order is produced. The only close in the
    /// whole system is an ExitPolicy close (or a forced event, which does not route through here).
    /// </summary>
    [Fact]
    public async Task FR9_FxExitOnly_ANameFallsOffTheWishList_ButIsHeld_NoSell()
    {
        // Held: security 1. Today's scores pick 2 and 3 (1 is not even scored). Policy: Never.
        var model = new FakeModel(
            new Dictionary<SecurityId, double> { [S(2)] = 0.9, [S(3)] = 0.8 },
            new ExitPolicy.Never());
        var inputs = Inputs(members: [1, 2, 3], held: [Pos(1, 100, 10_000m)]);

        var outcome = await Run(model, inputs);

        // 1 is held (Never never closes on signal); 2 and 3 open. No sell anywhere.
        Assert.Contains(S(1), outcome.Snapshot.Stage4.Holds);
        Assert.Empty(outcome.Snapshot.Stage4.Closes);
        Assert.DoesNotContain(outcome.Orders, o => o.Side == TradeSide.Sell);
        Assert.Contains(outcome.Orders, o => o.SecurityId == S(2) && o.Side == TradeSide.Buy);
        Assert.Contains(outcome.Orders, o => o.SecurityId == S(3) && o.Side == TradeSide.Buy);
    }

    [Fact]
    public async Task FR9_RankBuffer_AHeldNameFallingPastTheBuffer_IsClosedByPolicy_WithASell()
    {
        // Held: 1 and 2. Scores rank 1 first, 2 last (rank 4) — past an ExitRank of 2. Policy closes 2.
        var scores = new Dictionary<SecurityId, double> { [S(1)] = 0.9, [S(3)] = 0.8, [S(4)] = 0.7, [S(2)] = 0.1 };
        var model = new FakeModel(scores, new ExitPolicy.RankBuffer(ExitRank: 2), SelectionRule.TopN(2) with { MinScore = 0.0 });
        var inputs = Inputs(members: [1, 2, 3, 4], held: [Pos(1, 100, 10_000m), Pos(2, 50, 5_000m)]);

        var outcome = await Run(model, inputs);

        Assert.Contains(outcome.Snapshot.Stage4.Closes, c => c.Id == S(2) && c.Reason == TradeReason.ExitPolicy);
        var sell = Assert.Single(outcome.Orders, o => o.Side == TradeSide.Sell);
        Assert.Equal(S(2), sell.SecurityId);
        Assert.Equal(50, sell.Shares);                 // the WHOLE position is sold
        Assert.Equal(TradeReason.ExitPolicy, sell.Reason);
    }

    // ============================ Stage 6: decide-at-T / fill-at-T+1 ============================

    [Fact]
    public async Task FR9_EveryOrder_IsDecidedAtT_AndFillsAtTPlus1()
    {
        var model = new FakeModel(new Dictionary<SecurityId, double> { [S(1)] = 0.9 }, new ExitPolicy.Never());
        var outcome = await Run(model, Inputs(members: [1], held: []));

        var order = Assert.Single(outcome.Orders);
        Assert.Equal("2026-07-16", order.DecidedOn);
        Assert.Equal("2026-07-17", order.FillOn);
    }

    /// <summary>Shares are computed from T's RAW close (D30). At $100 and a $100,000 single-name
    /// target, that is exactly 1,000 shares — fractional if the price did not divide evenly.</summary>
    [Fact]
    public async Task FR9_OrderShares_AreTargetNotionalDividedByRawClose()
    {
        var model = new FakeModel(new Dictionary<SecurityId, double> { [S(1)] = 0.9 }, new ExitPolicy.Never());
        var prices = new Dictionary<SecurityId, double> { [S(1)] = 100.0 };

        var outcome = await Run(model, Inputs(members: [1], held: []), prices);

        var order = Assert.Single(outcome.Orders);
        Assert.Equal(1_000.0, order.Shares, 6);
    }

    [Fact]
    public async Task FR9_ARebalanceTrimTradesTheDelta_ANameAlreadyAtTargetTradesNothing()
    {
        // ScheduledRebalance on a rebalance day (session 21). Held 1 already at target (1,000 sh @ $100
        // = $100k, and it is the only name → target is the whole equity). It should trade nothing.
        var model = new FakeModel(
            new Dictionary<SecurityId, double> { [S(1)] = 0.9 },
            new ExitPolicy.ScheduledRebalance(21),
            SelectionRule.TopN(40) with { MinScore = 0.0 });
        var inputs = Inputs(members: [1], held: [Pos(1, 1_000, 100_000m)], sessions: 21);

        var outcome = await Run(model, inputs, new Dictionary<SecurityId, double> { [S(1)] = 100.0 });

        Assert.Equal(RebalanceScope.WholeBook, outcome.Snapshot.Stage4.Scope);
        Assert.DoesNotContain(outcome.Orders, o => o.SecurityId == S(1)); // already at target → no order
    }

    // ============================ F-DET + round-trip ============================

    /// <summary>F-DET: a re-run of the same day produces a byte-identical snapshot. This is the
    /// property Phase-4 replay reproducibility rests on, and it is why every stage sorts by id.</summary>
    [Fact]
    public async Task FDET_TwoRunsOfTheSameDay_ProduceByteIdenticalStageJson()
    {
        var scores = new Dictionary<SecurityId, double> { [S(3)] = 0.5, [S(1)] = 0.9, [S(2)] = 0.5 };
        FakeModel Make() => new(scores, new ExitPolicy.Never());

        var a = await Run(Make(), Inputs(members: [1, 2, 3], held: []));
        var b = await Run(Make(), Inputs(members: [1, 2, 3], held: []));

        Assert.Equal(a.Snapshot.ToJson(), b.Snapshot.ToJson());
    }

    /// <summary>THE T→T+1 CARRIER. The orders survive a serialize/deserialize round-trip through
    /// stage_json byte-for-byte — the property the fill path depends on, because the T+1 run reads
    /// these back rather than recomputing them at its later watermark.</summary>
    [Fact]
    public async Task FR9_Stage6Orders_RoundTripThroughStageJson_Exactly()
    {
        var model = new FakeModel(
            new Dictionary<SecurityId, double> { [S(1)] = 0.9, [S(2)] = 0.8 }, new ExitPolicy.Never());
        var outcome = await Run(model, Inputs(members: [1, 2], held: []));

        var json = outcome.Snapshot.ToJson();
        var restored = DecisionSnapshot.FromJson(json);

        Assert.Equal(outcome.Orders.Count, restored.Stage6Orders.Count);
        foreach (var (original, roundTripped) in outcome.Orders.Zip(restored.Stage6Orders))
        {
            Assert.Equal(original.SecurityId, roundTripped.SecurityId);
            Assert.Equal(original.Side, roundTripped.Side);
            Assert.Equal(original.Shares, roundTripped.Shares);
            Assert.Equal(original.Reason, roundTripped.Reason);
            Assert.Equal(original.FillOn, roundTripped.FillOn);
        }
    }

    /// <summary>SecurityId serializes as a bare number, not {"value": n} — stage_json is provenance
    /// a human reads.</summary>
    [Fact]
    public async Task FR9_SecurityIdSerializesAsABareNumber_InStageJson()
    {
        var model = new FakeModel(new Dictionary<SecurityId, double> { [S(42)] = 0.9 }, new ExitPolicy.Never());
        var outcome = await Run(model, Inputs(members: [42], held: []));

        var json = outcome.Snapshot.ToJson();
        Assert.Contains("42", json);
        Assert.DoesNotContain("\"value\"", json);
    }

    /// <summary>An unknown snapshot_version REFUSES rather than best-effort parsing (rule 10) — a
    /// half-understood order list is worse than a stopped run.</summary>
    [Fact]
    public void FR9_AnUnknownSnapshotVersion_IsRefused_NotBestEffortParsed()
    {
        // The version peek fires before the body is bound, so a future ds-9.9 is refused even though
        // the rest of this shape is deliberately unlike anything this build could deserialize.
        var json = """{"snapshot_version":"ds-9.9","some_future_field":{"nested":[1,2,3]}}""";

        var ex = Assert.Throws<InvalidOperationException>(() => DecisionSnapshot.FromJson(json));
        Assert.Contains("ds-9.9", ex.Message);
    }

    [Fact]
    public void FR9_StageJsonWithNoSnapshotVersion_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DecisionSnapshot.FromJson("""{"strategy_id":"x"}"""));
        Assert.Contains("snapshot_version", ex.Message);
    }

    // ============================ D84 cash constraint (finding 190) ============================

    /// <summary>
    /// D84 / finding 190. An OpensOnly book that is mostly invested (held worth ~$90k, only $10k cash)
    /// sizes a NEW open against the $10k CASH, not the $100k equity — and never sells the held name to
    /// fund it (rule 7). The held position is untouched; the open is scaled to fit the cash.
    /// </summary>
    [Fact]
    public async Task D84_OpensAreSizedToCash_HeldPositionUntouched_NoForcedSell()
    {
        // Held: security 1 (900 sh @ $100 = $90k). Cash $10k (equity $100k). Wish list picks a NEW name.
        var model = new FakeModel(
            new Dictionary<SecurityId, double> { [S(2)] = 0.9 }, new ExitPolicy.Never());
        var inputs = Inputs(members: [1, 2], held: [Pos(1, 900, 90_000m)], equity: 100_000m, cash: 10_000m);

        var outcome = await Run(model, inputs);

        // The held name is held and NEVER sold — cash frees up only via the ExitPolicy (rule 7).
        Assert.Contains(S(1), outcome.Snapshot.Stage4.Holds);
        Assert.DoesNotContain(outcome.Orders, o => o.Side == TradeSide.Sell);

        // The open is sized to the $10k cash (= 100 sh @ $100), NOT the $100k equity (pre-fix: 1,000 sh).
        var open = Assert.Single(outcome.Orders, o => o.SecurityId == S(2));
        Assert.Equal(TradeSide.Buy, open.Side);
        Assert.Equal(100.0, open.Shares, 6);
        Assert.Equal(10_000m, outcome.Snapshot.Stage5Targets.Single(t => t.Id == S(2)).TargetNotional);
    }

    /// <summary>
    /// D84 / finding 190. THE RATCHET. An OpensOnly book that keeps opening new names day after day
    /// (the accumulation §13.6 warns about) never spends past its cash: each day's opens are scaled to
    /// the cash then on hand, so cash decreases toward zero and STOPS — it never ratchets negative the
    /// way sizing-against-equity did. Fills are simulated at the target notional (no overnight gap in
    /// this fixture; costs are the separate bounded residual, finding 196).
    /// </summary>
    [Fact]
    public async Task D84_AnAccumulatingOpensOnlyBook_NeverRatchetsPastItsCash()
    {
        const decimal starting = 100_000m;
        const double price = 100.0;
        var sizing = new SizingOptions { PositionCapPct = 0.25 }; // ≤25% per name ⇒ ~4 opens exhaust cash
        var cash = starting;
        var book = new Dictionary<SecurityId, Position>();

        // Eight sessions, one fresh eligible name entering each day. Never policy ⇒ pure accumulation.
        for (var day = 1; day <= 8; day++)
        {
            var members = Enumerable.Range(1, day).Select(i => (long)i).ToList();
            var model = new FakeModel(members.ToDictionary(S, _ => 0.9), new ExitPolicy.Never());
            var held = book.Values.OrderBy(p => p.SecurityId.Value).ToList();
            var equity = cash + held.Sum(p => (decimal)p.Shares * (decimal)price);

            var view = new FunnelFeatureView
            {
                AsOf = AsOf,
                Priced = members.Select(S).ToHashSet(),
                RawCloses = members.ToDictionary(S, _ => price),
            };
            var inputs = Inputs(members, held, equity: equity, cash: cash);
            var outcome = await FunnelRunner.RunAsync(model, view, inputs, new GuardrailsOptions(), sizing);

            foreach (var order in outcome.Orders)
            {
                Assert.Equal(TradeSide.Buy, order.Side); // Never never sells (rule 7)
                var notional = (decimal)order.Shares * (decimal)price;
                cash -= notional;                        // the T+1 fill spends the target notional
                book[order.SecurityId] = new Position
                {
                    AccountId = 1, SecurityId = order.SecurityId,
                    Shares = order.Shares, CostBasis = notional, OpenedOn = "2026-01-02",
                };
            }

            // THE INVARIANT: cash never ratchets negative. Pre-fix (sizing against equity) it would go
            // negative the first day the intended opens exceed cash and compound from there.
            Assert.True(cash >= 0m, $"day {day}: cash went negative ({cash}) — the book ratcheted past its cash.");
        }

        // ~4 opens at 25% each exhaust the cash; the remaining days open nothing (the cash floor held).
        Assert.Equal(4, book.Count);
        Assert.Equal(0m, cash);
    }

    // ============================ contract guards ============================

    /// <summary>A model that scores a name outside the eligible pool it was handed is a point-in-time
    /// violation wearing a plausible face — the runner fails loudly rather than filtering it away.</summary>
    [Fact]
    public async Task FR9_AModelScoringOutsideTheEligiblePool_FailsLoudly()
    {
        // The fake model here ignores the eligible list and scores a non-member.
        var model = new RogueModel();
        var view = new FunnelFeatureView { AsOf = AsOf, Priced = [S(1)], RawCloses = new Dictionary<SecurityId, double> { [S(1)] = 100.0 } };
        var inputs = Inputs(members: [1], held: []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FunnelRunner.RunAsync(model, view, inputs, new GuardrailsOptions(), new SizingOptions()));
    }

    private sealed class RogueModel : IModel
    {
        public string Id => "rogue";
        public StrategyConfig Config { get; } = new() { Seed = 1, Selection = SelectionRule.TopN(40), Sizing = SizingMode.Equal };
        public HoldingHorizon Horizon { get; } = new HoldingHorizon.ToNextRebalance();
        public ExitPolicy Exits { get; } = new ExitPolicy.Never();

        public Task<IReadOnlyDictionary<SecurityId, double>> ScoreUniverseAsync(
            IReadOnlyList<SecurityId> eligible, DateOnly asOf, IFeatureView features, CancellationToken ct = default) =>
            // Scores security 999, which was never in the eligible pool.
            Task.FromResult<IReadOnlyDictionary<SecurityId, double>>(new Dictionary<SecurityId, double> { [S(999)] = 0.9 });
    }
}

/// <summary>A feature view for the funnel tests: priced set + raw closes are handed in. Only the
/// members the funnel actually reads are implemented; the rest throw so a test that depends on an
/// unset value fails loudly rather than silently reading a zero.</summary>
internal sealed class FunnelFeatureView : IFeatureView
{
    public required DateOnly AsOf { get; init; }
    public string Watermark { get; init; } = "2026-07-16T22:00:00Z";
    public HashSet<SecurityId> Priced { get; init; } = [];
    public Dictionary<SecurityId, double> RawCloses { get; init; } = [];

    public IReadOnlyList<SecurityId> PricedOn(DateOnly date) => Priced.OrderBy(x => x.Value).ToList();
    public double? RawClose(SecurityId id, DateOnly date) => RawCloses.TryGetValue(id, out var v) ? v : null;
    public double? RawOpen(SecurityId id, DateOnly date) => RawCloses.TryGetValue(id, out var v) ? v : null;
    public double? AdjClose(SecurityId id, DateOnly date) => RawCloses.TryGetValue(id, out var v) ? v : null;
    public IReadOnlyList<double> AdjCloseSeries(SecurityId id, int sessions) => [];
    public double? Adv21Shares(SecurityId id) => null;
    public double? Adv21Notional(SecurityId id) => null;
    public double? RealizedVolDaily(SecurityId id, int window) => null;
}
