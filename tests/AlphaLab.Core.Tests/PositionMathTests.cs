using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// The sell-side book arithmetic and its oversell refusal (D142).
///
/// These are the fixtures that make the guard FALSIFIABLE. They construct the impossible book state
/// directly and have never heard of a corporate action, so they keep proving the guard fires long after
/// the restatement has made it unreachable through the pipeline — which is the point of a backstop that
/// nobody has seen go red.
/// </summary>
public class PositionMathTests
{
    private static Position Held(double shares, decimal basis = 1000m) => new()
    {
        AccountId = 7,
        SecurityId = new SecurityId(42),
        Shares = shares,
        CostBasis = basis,
        OpenedOn = "2026-01-05",
    };

    [Fact]
    public void FR9_D142_ASellLargerThanTheBook_IsRefused_NotAbsorbedAsAClose()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PositionMath.ApplySell(Held(50.0), 100.0));

        // The message has to name BOTH counts and the overshoot: the operator's first question is
        // "by how much", and a bare "oversell" would send them to the DB to find out.
        Assert.Contains("100", ex.Message, StringComparison.Ordinal);
        Assert.Contains("50", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OVERSELL", ex.Message, StringComparison.Ordinal);
        Assert.Contains("42", ex.Message, StringComparison.Ordinal);   // the security
        Assert.Contains("7", ex.Message, StringComparison.Ordinal);    // the account
    }

    [Fact]
    public void FR9_D142_ASellOfTheWholeLine_ClosesItExactly()
    {
        var after = PositionMath.ApplySell(Held(50.0), 50.0);

        Assert.Equal(0.0, after.Shares);
        Assert.Equal(1000m, after.CostBasis);   // a close does not reduce basis; the row goes away
    }

    [Fact]
    public void FR9_D142_ASellWithinFloatingPointNoiseOfTheLine_StillCloses()
    {
        // The epsilon band survives the guard: a remainder of -5e-10 is noise, not an oversell. This is
        // the assertion that fails if someone "tightens" the refusal to `newShares < 0`.
        var after = PositionMath.ApplySell(Held(50.0), 50.0 + 5e-10);

        Assert.Equal(0.0, after.Shares);
    }

    [Fact]
    public void FR9_D142_AnOversellJustBeyondTheNoiseBand_IsRefused()
    {
        // The other side of the same boundary, so the band is pinned from both directions rather than
        // only from the permissive one.
        Assert.Throws<InvalidOperationException>(
            () => PositionMath.ApplySell(Held(50.0), 50.0 + 1e-6));
    }

    [Fact]
    public void FR9_D142_APartialSell_ReducesBasisProportionally()
    {
        var after = PositionMath.ApplySell(Held(50.0, 1000m), 20.0);

        Assert.Equal(30.0, after.Shares);
        Assert.Equal(BasisMath.ReduceForSale(1000m, 30.0, 50.0), after.CostBasis);
        Assert.Equal(600m, after.CostBasis);
    }

    [Fact]
    public void FR9_D142_ASellPreservesEverythingElseOnTheRow()
    {
        // Including Frozen — the sell leg has always carried it, and this pins that it keeps doing so
        // while the arithmetic moves house.
        var frozen = Held(50.0) with { Frozen = true, FrozenReason = "unmapped action" };

        var after = PositionMath.ApplySell(frozen, 20.0);

        Assert.True(after.Frozen);
        Assert.Equal("unmapped action", after.FrozenReason);
        Assert.Equal("2026-01-05", after.OpenedOn);
        Assert.Equal(7, after.AccountId);
    }

    [Fact]
    public void FR9_D142_TheShareToleranceIsOneNumber_SharedWithTheFunnel()
    {
        // OrderBuilder reads this constant for its smallest routable delta. If the two ever diverge, an
        // order can be routed for a quantity the ledger will not recognise as a close. There is exactly
        // one definition, and this is the test that says so out loud.
        Assert.Equal(1e-9, PositionMath.ShareEpsilon);
    }
}
