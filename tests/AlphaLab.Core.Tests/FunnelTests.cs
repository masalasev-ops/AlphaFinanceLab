using AlphaLab.Core.Config;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Funnel;

namespace AlphaLab.Core.Tests;

/// <summary>
/// A hand-built <see cref="IFeatureView"/> — the fixture library's "deterministic builders: code, not
/// CSVs". The funnel stages are pure, so the whole of Stages 1/3/5 is exercised here with no SQLite,
/// no watermark plumbing, and no provider. The real adapter (BarFeatureView) is tested separately
/// against the versioned store, including F-LEAK.
/// </summary>
internal sealed class FakeFeatureView : IFeatureView
{
    public required DateOnly AsOf { get; init; }
    public string Watermark { get; init; } = "2026-07-16T22:00:00Z";

    /// <summary>Securities that have a usable price on AsOf.</summary>
    public HashSet<SecurityId> Priced { get; init; } = [];

    public Dictionary<SecurityId, double> Adv21SharesBy { get; init; } = [];

    public IReadOnlyList<SecurityId> PricedOn(DateOnly date) => Priced.OrderBy(x => x.Value).ToList();
    public double? AdjClose(SecurityId id, DateOnly date) => Priced.Contains(id) ? 100.0 : null;
    public IReadOnlyList<double> AdjCloseSeries(SecurityId id, int sessions) => [];
    public double? RawClose(SecurityId id, DateOnly date) => Priced.Contains(id) ? 100.0 : null;
    public double? RawOpen(SecurityId id, DateOnly date) => Priced.Contains(id) ? 100.0 : null;
    public double? Adv21Shares(SecurityId id) => Adv21SharesBy.TryGetValue(id, out var v) ? v : null;
    public double? Adv21Notional(SecurityId id) => null;
    public double? RealizedVolDaily(SecurityId id, int window) => null;
}

/// <summary>
/// Stage 1 (eligibility), Stage 3 (selection), Stage 5 (sizing) — MASTER §6, catalog §3.
/// Fixtures: FX-ZeroScore (the no-padding invariant) and F-DET (deterministic ordering).
/// </summary>
public class FunnelTests
{
    private static readonly DateOnly AsOf = new(2026, 7, 16);
    private static SecurityId S(long id) => new(id);
    private static GuardrailsOptions Rails() => new();

    // ============================ Stage 1 — eligibility ============================

    [Fact]
    public void FR7_Stage1_EligiblePool_IsTheInIndexNamesThatArePriced()
    {
        var view = new FakeFeatureView { AsOf = AsOf, Priced = [S(1), S(2), S(4)] };

        var result = Eligibility.Resolve([S(1), S(2), S(3)], AsOf, view);

        Assert.Equal([S(1), S(2)], result.Eligible);          // 3 is a member but unpriced
        Assert.DoesNotContain(S(4), result.Eligible);          // 4 is priced but not a member
        var dropped = Assert.Single(result.Excluded);
        Assert.Equal(S(3), dropped.Id);
        Assert.Contains("not priced", dropped.Reason);
    }

    // F-DET: the pool must not depend on the order the roster arrived in, or stage_json differs
    // between two runs of the same day and nothing downstream reproduces.
    [Fact]
    public void FR7_Stage1_PoolIsOrderedBySecurityId_RegardlessOfRosterOrder()
    {
        var view = new FakeFeatureView { AsOf = AsOf, Priced = [S(1), S(2), S(3)] };

        var forward = Eligibility.Resolve([S(1), S(2), S(3)], AsOf, view);
        var shuffled = Eligibility.Resolve([S(3), S(1), S(2)], AsOf, view);

        Assert.Equal([S(1), S(2), S(3)], forward.Eligible);
        Assert.Equal(forward.Eligible, shuffled.Eligible);
    }

    [Fact]
    public void FR7_Stage1_DuplicateRosterEntries_CollapseToOne()
    {
        var view = new FakeFeatureView { AsOf = AsOf, Priced = [S(1)] };

        var result = Eligibility.Resolve([S(1), S(1)], AsOf, view);

        Assert.Equal([S(1)], result.Eligible);
    }

    // Rule 4: a view built for another day cannot answer for this one. A wiring bug, not an absence.
    [Fact]
    public void FR7_Stage1_FeatureViewForAnotherDay_Throws()
    {
        var view = new FakeFeatureView { AsOf = AsOf.AddDays(-1), Priced = [S(1)] };

        var ex = Assert.Throws<ArgumentException>(() => Eligibility.Resolve([S(1)], AsOf, view));
        Assert.Contains("2026-07-15", ex.Message);
        Assert.Contains("2026-07-16", ex.Message);
    }

    /// <summary>
    /// PROPOSAL P1, PINNED AS A TEST. Stage 1's `liquid` gate filters NOTHING, and this test exists so
    /// that fact is executable rather than a comment someone skims.
    ///
    /// A name with an ADV of 1 share/day — as illiquid as a listed security gets — is fully eligible.
    /// That is CORRECT for the S&P 100 slice (every member is liquid; a gate would remove nobody) and
    /// it is what the design actually says: §6's diagram lists the word `liquid`, D20 says "Liquid
    /// default", and D43's liquidity bucket prices a fill rather than gating a name. No Eligibility
    /// threshold exists to read and inventing one would be a design decision in disguise.
    ///
    /// IF THIS TEST FAILS, someone added a liquidity screen. That may well be right — but it needs a
    /// CONFIG key and a D-number first (P1), and it must be settled BEFORE the D70 sp500 widening,
    /// where the tail genuinely is less liquid and a no-op gate becomes a silent wrong answer.
    /// </summary>
    [Fact]
    public void FR7_Stage1_LiquidGateFiltersNothing_AVanishinglyIlliquidNameIsStillEligible()
    {
        var view = new FakeFeatureView
        {
            AsOf = AsOf,
            Priced = [S(1)],
            Adv21SharesBy = { [S(1)] = 1.0 }, // one share a day
        };

        var result = Eligibility.Resolve([S(1)], AsOf, view);

        Assert.Equal([S(1)], result.Eligible);
        Assert.Empty(result.Excluded);
    }

    // ============================ Stage 3 — selection ============================

    /// <summary>FX-ZeroScore, verbatim from TEST_PLAN: a day where only 2 names score > 0 with N = 40
    /// yields a TWO-name wish list and cash — never 40 names padded out of thin air.</summary>
    [Fact]
    public void FR8_FxZeroScore_OnlyTwoNamesScorePositive_WishListIsTwo_NoPadding()
    {
        var scores = Enumerable.Range(1, 50).ToDictionary(i => S(i), i => 0.0);
        scores[S(7)] = 0.9;
        scores[S(23)] = 0.8;

        var result = Selection.Select(scores, SelectionRule.TopN(40) with { MinScore = 0.0 }, Rails(), seed: 0);

        Assert.Equal([S(7), S(23)], result.WishList);   // best-first
        Assert.Equal(48, result.Excluded.Count);
        Assert.All(result.Excluded, e => Assert.Contains("not > 0", e.Reason));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void FR8_AZeroNegativeOrUnscoreableName_IsNeverSelectable(double score)
    {
        var scores = new Dictionary<SecurityId, double> { [S(1)] = score, [S(2)] = 0.7 };

        // MinScore floored at 0 so ONLY the zero-score invariant can be doing the work here.
        var result = Selection.Select(scores, SelectionRule.TopN(40) with { MinScore = 0.0 }, Rails(), seed: 0);

        Assert.Equal([S(2)], result.WishList);
        Assert.Contains(result.Excluded, e => e.Id == S(1) && e.Reason.Contains("not > 0"));
    }

    [Fact]
    public void FR8_TopN_KeepsTheNHighestScores_BestFirst()
    {
        var scores = new Dictionary<SecurityId, double>
        {
            [S(1)] = 0.10, [S(2)] = 0.90, [S(3)] = 0.50, [S(4)] = 0.70,
        };

        var result = Selection.Select(scores, SelectionRule.TopN(2) with { MinScore = 0.0 }, Rails(), seed: 0);

        Assert.Equal([S(2), S(4)], result.WishList);
        Assert.Contains(result.Excluded, e => e.Id == S(3) && e.Reason.Contains("breadth cap"));
    }

    // FX-SeededTieBreak (catalog §3). REPLACES FR8_TiedScores_BreakBySecurityId_Deterministically,
    // which asserted ascending security_id — the order the catalog names as forbidden ("never by
    // ingestion order"). Determinism was necessary and not sufficient: the old rule was stable AND
    // biased, and the test's expected value encoded the bias.
    [Fact]
    public void FR8_TiedScores_BreakByTheSeededStableOrder_DeterministicallyAndNotByIngestionOrder()
    {
        var scores = new Dictionary<SecurityId, double> { [S(9)] = 0.5, [S(2)] = 0.5, [S(5)] = 0.5 };

        var result = Selection.Select(scores, SelectionRule.TopN(2) with { MinScore = 0.0 }, Rails(), seed: 7);

        // Deterministic: the SAME seed reproduces the same wish list, run after run (F-DET).
        Assert.Equal(result.WishList, Selection.Select(scores, SelectionRule.TopN(2) with { MinScore = 0.0 }, Rails(), seed: 7).WishList);
        Assert.Equal(2, result.WishList.Count);

        // ...and NOT ascending security_id, which is what the shipped rule produced and what the
        // catalog forbids. Asserted as a property rather than by pinning a hash literal: pinning one
        // would make the fixture a restatement of the implementation instead of a check on it.
        Assert.NotEqual([S(2), S(5)], result.WishList);
    }

    // The other half of the seeded order: two strategies differing ONLY in Config.Seed must draw
    // differently among equals. Under the ascending-id rule the seed did nothing at all, which is why
    // a binary scorer's book was a fact about the security master rather than about the strategy.
    [Fact]
    public void FR8_SeededTieBreak_DifferentSeedsDrawDifferentNamesFromTheSameTiedBlock()
    {
        var scores = Enumerable.Range(1, 40).ToDictionary(i => S(i), _ => 1.0);
        var rule = SelectionRule.TopN(5) with { MinScore = 0.0 };

        var a = Selection.Select(scores, rule, Rails(), seed: 1).WishList;
        var b = Selection.Select(scores, rule, Rails(), seed: 2).WishList;

        Assert.Equal(5, a.Count);
        Assert.Equal(5, b.Count);
        Assert.NotEqual(a, b);
    }

    // The invariant FunnelRunner.RanksOf states in prose ("the tiebreak is the same one Selection
    // uses, and it must be") — untested until 6.3, because ascending id gave the two sites no way to
    // disagree. A seeded order does: a name could otherwise sit inside the top N by one rule and past
    // its RankBuffer exit by the other, opening and closing on the same day.
    [Fact]
    public void FR8_SeededTieBreak_SelectionAndRanksOf_AgreeOnTheSameOrder()
    {
        var scores = Enumerable.Range(1, 30).ToDictionary(i => S(i), i => i <= 20 ? 1.0 : 0.25);
        const int seed = 11;

        var wishList = Selection.Select(scores, SelectionRule.TopN(8) with { MinScore = 0.0 }, Rails(), seed).WishList;
        var ranks = FunnelRunner.RanksOf(scores, seed);

        // The wish list IS the first 8 of the rank order — same comparator, same result.
        Assert.Equal(wishList, scores.Keys.OrderBy(id => ranks[id]).Take(8).ToList());
    }

    [Fact]
    public void FR8_Threshold_KeepsScoresAtOrAboveMinScore_CappedAtMaxConcurrent()
    {
        var scores = new Dictionary<SecurityId, double>
        {
            [S(1)] = 0.59, [S(2)] = 0.60, [S(3)] = 0.95, [S(4)] = 0.80,
        };

        var result = Selection.Select(scores, SelectionRule.Threshold(0.60, maxConcurrent: 2), Rails(), seed: 0);

        Assert.Equal([S(3), S(4)], result.WishList);                                  // 0.60 makes the floor, but the cap bites
        Assert.Contains(result.Excluded, e => e.Id == S(1) && e.Reason.Contains("strategy floor"));
        Assert.Contains(result.Excluded, e => e.Id == S(2) && e.Reason.Contains("breadth cap"));
    }

    // ---- AllPositive (catalog §3; §6.6 TimeSeriesMomentum is its only declared consumer) ----

    [Fact]
    public void FR8_AllPositive_KeepsEveryNamePassingTheInvariant_CappedAtMaxConcurrent()
    {
        var scores = new Dictionary<SecurityId, double>
        {
            [S(1)] = 0.05, [S(2)] = 0.0, [S(3)] = 0.55, [S(4)] = 0.95, [S(5)] = -0.10,
        };

        var result = Selection.Select(scores, SelectionRule.AllPositive(maxConcurrent: 10), Rails(), seed: 0);

        // 0.05 and 0.55 are BELOW Threshold's 0.60 default and are kept anyway — that is the whole
        // difference. The zero and the negative are still refused: AllPositive wraps the invariant,
        // it does not exempt anything from it (hard rule 7).
        Assert.Equal([S(4), S(3), S(1)], result.WishList);
        Assert.Contains(result.Excluded, e => e.Id == S(2) && e.Reason.Contains("not > 0"));
        Assert.Contains(result.Excluded, e => e.Id == S(5) && e.Reason.Contains("not > 0"));
    }

    // The factory must pin MinScore = 0.0 explicitly: the record's default is 0.60 and Selection
    // applies the strategy floor in EVERY mode, so a rule built without it would be Threshold(0.60)
    // wearing AllPositive's name — keeping only names above 0.60 while claiming to keep every
    // positive one.
    [Fact]
    public void FR8_AllPositive_CarriesNoStrategyFloorOfItsOwn_NotTheRecordsDefault()
    {
        Assert.Equal(0.0, SelectionRule.AllPositive(50).MinScore);
    }

    // §6.6's stated behaviour: "a broadly falling fixture reduces the invested count (cash-weight
    // rises)". This is FX-ZeroScore's shape for the new mode — going to cash is the strategy working.
    [Fact]
    public void FR8_AllPositive_ABroadlyFallingCrossSection_ShortensTheWishList_AndHoldsCash()
    {
        var bull = Enumerable.Range(1, 40).ToDictionary(i => S(i), _ => 1.0);
        var bear = Enumerable.Range(1, 40).ToDictionary(i => S(i), i => i <= 3 ? 1.0 : 0.0);
        var rule = SelectionRule.AllPositive(maxConcurrent: 50);

        Assert.Equal(40, Selection.Select(bull, rule, new GuardrailsOptions { MaxConcurrentPositions = 100 }, seed: 0).WishList.Count);
        Assert.Equal(3, Selection.Select(bear, rule, new GuardrailsOptions { MaxConcurrentPositions = 100 }, seed: 0).WishList.Count);
    }

    // AllPositive + a binary scorer is the case that makes the seeded tie-break load-bearing rather
    // than a boundary detail: EVERY selected name is tied at 1.0, so the tie order IS the selection
    // rule whenever the qualifying count exceeds the cap.
    [Fact]
    public void FR8_AllPositive_WithABinaryScorer_TheSeededOrderDecidesTheWholeBook()
    {
        var scores = Enumerable.Range(1, 80).ToDictionary(i => S(i), _ => 1.0);
        var rule = SelectionRule.AllPositive(maxConcurrent: 10);

        var a = Selection.Select(scores, rule, new GuardrailsOptions { MaxConcurrentPositions = 100 }, seed: 3).WishList;
        var b = Selection.Select(scores, rule, new GuardrailsOptions { MaxConcurrentPositions = 100 }, seed: 4).WishList;

        Assert.Equal(10, a.Count);
        Assert.NotEqual(a, b);
        // Not the lowest-numbered ten — the book is a fact about the strategy, not the security master.
        Assert.NotEqual(Enumerable.Range(1, 10).Select(i => S(i)).ToList(), a);
    }

    [Fact]
    public void FR8_SystemFloor_GuardrailsMinScore_SitsBeneathTheStrategyFloor()
    {
        var scores = new Dictionary<SecurityId, double> { [S(1)] = 0.30, [S(2)] = 0.80 };
        var rails = new GuardrailsOptions { MinScore = 0.50 };

        var result = Selection.Select(scores, SelectionRule.TopN(40) with { MinScore = 0.0 }, rails, seed: 0);

        Assert.Equal([S(2)], result.WishList);
        Assert.Contains(result.Excluded, e => e.Id == S(1) && e.Reason.Contains("Guardrails.MinScore"));
    }

    [Fact]
    public void FR8_GuardrailsMaxConcurrentPositions_CapsBelowTheStrategysOwnRule()
    {
        var scores = Enumerable.Range(1, 10).ToDictionary(i => S(i), i => 1.0 - i * 0.01);
        var rails = new GuardrailsOptions { MaxConcurrentPositions = 3 };

        var result = Selection.Select(scores, SelectionRule.TopN(40) with { MinScore = 0.0 }, rails, seed: 0);

        Assert.Equal(3, result.WishList.Count);
    }

    // A silent drop is indistinguishable from a bug a year later; stage_json must be able to answer
    // "why wasn't X considered?" for every name that was scored.
    [Fact]
    public void FR8_EveryScoredNameIsEitherOnTheWishListOrExcludedWithAReason()
    {
        var scores = new Dictionary<SecurityId, double>
        {
            [S(1)] = 0.0, [S(2)] = 0.30, [S(3)] = 0.95, [S(4)] = 0.90, [S(5)] = double.NaN,
        };

        var result = Selection.Select(scores, SelectionRule.TopN(1), Rails(), seed: 0);

        var accounted = result.WishList.Concat(result.Excluded.Select(e => e.Id)).ToList();
        Assert.Equal(scores.Count, accounted.Count);
        Assert.Equal(scores.Keys.OrderBy(k => k.Value), accounted.OrderBy(k => k.Value));
        Assert.All(result.Excluded, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
    }

    // ============================ Stage 5 — sizing ============================

    // The cap is lifted here so this test measures ONLY the equal-weight split. Note what the default
    // implies, since it is easy to miss: at Sizing.PositionCapPct = 0.05 an equal-weight book needs at
    // least 20 names to be fully invested — a 4-name book would be clamped to 20% invested. The two
    // tests below pin both sides of that.
    [Fact]
    public void FR11_EqualWeight_SplitsEquityEvenlyAcrossTheTargets()
    {
        var options = new SizingOptions { PositionCapPct = 1.0 };

        var result = Sizing.Size([S(1), S(2), S(3), S(4)], 100_000m, 100_000m, SizingMode.Equal, options);

        Assert.All(result.Targets, t => Assert.Equal(25_000m, t.TargetNotional));
        Assert.Equal(0m, result.UninvestedCash);
    }

    /// <summary>The cap clamps and LEAVES CASH — it never redistributes the excess, because
    /// redistributing would push the remaining names back over the same cap.</summary>
    [Fact]
    public void FR11_PositionCap_ClampsAndLeavesCash_RatherThanRedistributing()
    {
        // 4 names would be 25% each; the 5% cap clamps every one to $5,000 => $20,000 invested.
        var options = new SizingOptions { PositionCapPct = 0.05 };

        var result = Sizing.Size([S(1), S(2), S(3), S(4)], 100_000m, 100_000m, SizingMode.Equal, options);

        Assert.All(result.Targets, t => Assert.Equal(5_000m, t.TargetNotional));
        Assert.Equal(80_000m, result.UninvestedCash);
    }

    [Fact]
    public void FR11_PositionCap_DoesNotBiteWhenTheBookIsBroad()
    {
        // 40 names at 2.5% each are all under the 5% cap.
        var targets = Enumerable.Range(1, 40).Select(i => S(i)).ToList();

        var result = Sizing.Size(targets, 100_000m, 100_000m, SizingMode.Equal, new SizingOptions { PositionCapPct = 0.05 });

        Assert.All(result.Targets, t => Assert.Equal(2_500m, t.TargetNotional));
        Assert.Equal(0m, result.UninvestedCash);
    }

    /// <summary>FR-11 is PARTIAL in Phase 2. The two unbuilt modes must REFUSE, not fall back to equal
    /// weight — a fallback would size every position by a rule the config does not claim, and the run
    /// would look healthy while the numbers were wrong.</summary>
    [Theory]
    [InlineData(SizingMode.InverseVol)]
    [InlineData(SizingMode.Kelly)]
    public void FR11_AnUnbuiltSizingMode_IsRefused_NeverSilentlyEqualWeighted(SizingMode mode)
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => Sizing.Size([S(1), S(2)], 100_000m, 100_000m, mode, new SizingOptions { Mode = mode }));

        Assert.Contains(mode.ToString(), ex.Message);
        Assert.Contains("Phase 6", ex.Message);
    }

    [Fact]
    public void FR11_AnEmptyWishList_IsAllCash_NotAnError()
    {
        var result = Sizing.Size([], 100_000m, 100_000m, SizingMode.Equal, new SizingOptions());

        Assert.Empty(result.Targets);
        Assert.Equal(100_000m, result.UninvestedCash);
    }

    // Rule 10: no equity, no orders — and say so per name, rather than returning a bare empty list
    // that reads like "the strategy wanted nothing today".
    [Fact]
    public void FR11_NonPositiveEquity_SizesNothing_AndSaysWhyPerName()
    {
        var result = Sizing.Size([S(1), S(2)], 0m, 0m, SizingMode.Equal, new SizingOptions());

        Assert.Empty(result.Targets);
        Assert.Equal(2, result.Excluded.Count);
        Assert.All(result.Excluded, e => Assert.Contains("not positive", e.Reason));
    }

    // A cap of zero can never produce a portfolio: that is a broken config, not a market condition,
    // and an all-cash lab that looks like it is running is the silent failure worth being loud about.
    [Fact]
    public void FR11_APositionCapOfZero_Throws_RatherThanHoldingCashForever()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Sizing.Size([S(1)], 100_000m, 100_000m, SizingMode.Equal, new SizingOptions { PositionCapPct = 0.0 }));
    }

    [Fact]
    public void FR11_TargetsAreOrderedBySecurityId_Deterministically()
    {
        var result = Sizing.Size([S(9), S(2), S(5)], 90_000m, 90_000m, SizingMode.Equal, new SizingOptions { PositionCapPct = 1.0 });

        Assert.Equal([S(2), S(5), S(9)], result.Targets.Select(t => t.Id));
    }

    // ---- D84 cash constraint (finding 190): new opens are sized against AVAILABLE CASH and scaled to
    //      fit, never total equity — so an account can never spend cash it does not hold. ----

    /// <summary>D84 / finding 190. An account with $10k cash whose wish list would (against $100k equity)
    /// spend $100k opens for EXACTLY $10k — every target scaled proportionally — never a cent more.</summary>
    [Fact]
    public void D84_OpensExceedingCash_SpendExactlyAvailableCash_NeverNegative()
    {
        // Against equity each of the 2 names would be 50k (=100k total); the cash ceiling scales both to
        // 5k so the total is exactly the 10k cash. Cap lifted so ONLY the cash constraint is at work.
        var result = Sizing.Size([S(1), S(2)], equity: 100_000m, availableCash: 10_000m,
            SizingMode.Equal, new SizingOptions { PositionCapPct = 1.0 });

        Assert.All(result.Targets, t => Assert.Equal(5_000m, t.TargetNotional));
        Assert.Equal(10_000m, result.Targets.Sum(t => t.TargetNotional)); // spends exactly the cash
        Assert.Equal(0m, result.UninvestedCash);                          // no cash left after opening
    }

    /// <summary>D84. On a near-zero (or drifted-negative) cash day nothing is opened — the correct sparse
    /// outcome, and NOT a forced sell of a held name (rule 7). Every wish-list name carries a reason.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-2_500)]
    public void D84_NoCashAvailable_OpensNothing_WithAReasonPerName(double cash)
    {
        var result = Sizing.Size([S(1), S(2), S(3)], equity: 100_000m, availableCash: (decimal)cash,
            SizingMode.Equal, new SizingOptions { PositionCapPct = 1.0 });

        Assert.Empty(result.Targets);
        Assert.Equal(3, result.Excluded.Count);
        Assert.All(result.Excluded, e => Assert.Contains("no cash available", e.Reason));
    }

    /// <summary>D84. The cash ceiling binds IN ADDITION to the per-name cap: the 5% cap sets each intended
    /// target to $5k (=$20k for 4 names), then $12k cash scales them to $3k each.</summary>
    [Fact]
    public void D84_CashConstraint_BindsInAdditionToPositionCap()
    {
        var result = Sizing.Size([S(1), S(2), S(3), S(4)], equity: 100_000m, availableCash: 12_000m,
            SizingMode.Equal, new SizingOptions { PositionCapPct = 0.05 });

        Assert.All(result.Targets, t => Assert.Equal(3_000m, t.TargetNotional)); // 5k capped, ×0.6 for cash
        Assert.Equal(12_000m, result.Targets.Sum(t => t.TargetNotional));
    }

    /// <summary>D84. When cash comfortably covers the capped book the ceiling does NOT bind — the pre-D84
    /// "cap and hold cash" behaviour is unchanged, and UninvestedCash reflects the spendable cash.</summary>
    [Fact]
    public void D84_CashAboveTheCappedBook_DoesNotBind_AndUninvestedCashIsHonest()
    {
        var result = Sizing.Size([S(1), S(2), S(3), S(4)], equity: 100_000m, availableCash: 30_000m,
            SizingMode.Equal, new SizingOptions { PositionCapPct = 0.05 });

        Assert.All(result.Targets, t => Assert.Equal(5_000m, t.TargetNotional)); // 5% cap, cash not binding
        Assert.Equal(10_000m, result.UninvestedCash);                            // 30k spendable − 20k deployed
    }
}
