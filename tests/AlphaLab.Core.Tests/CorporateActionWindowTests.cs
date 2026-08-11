using AlphaLab.Core.Ledger;

namespace AlphaLab.Core.Tests;

/// <summary>
/// The (previousSession, asOf] window (D142's single definition; finding 192's rule).
///
/// The property that matters is not any single case but the PARTITION: consecutive sessions must cover
/// the date line exactly once each, or an action is applied twice or not at all. Both consumers — the
/// book restatement and the pending-order restatement — read this, so a drift here desynchronises the
/// two halves of the fix.
/// </summary>
public class CorporateActionWindowTests
{
    private const string Prev = "2026-07-15";
    private const string AsOf = "2026-07-16";

    [Fact]
    public void FR9_D142_AnActionEffectiveOnTheSession_Applies()
    {
        Assert.True(CorporateActionWindow.Contains(AsOf, Prev, AsOf));
    }

    [Fact]
    public void FR9_D142_AnActionEffectiveOnThePreviousSession_DoesNot_ItAlreadyApplied()
    {
        Assert.False(CorporateActionWindow.Contains(Prev, Prev, AsOf));
    }

    [Fact]
    public void FR9_D142_AWeekendOrHolidayDate_LandsOnTheNextSession_NotDropped()
    {
        // Friday 2026-07-10 → Monday 2026-07-13: a Saturday effective date belongs to Monday's window.
        Assert.True(CorporateActionWindow.Contains("2026-07-11", "2026-07-10", "2026-07-13"));
    }

    [Fact]
    public void FR9_D142_AFutureDate_DoesNotApplyYet()
    {
        Assert.False(CorporateActionWindow.Contains("2026-07-17", Prev, AsOf));
    }

    [Fact]
    public void FR9_D142_AtTheFirstSession_TheWindowIsOpenOnTheLeft()
    {
        Assert.True(CorporateActionWindow.Contains("2001-01-01", previousSession: null, AsOf));
    }

    [Fact]
    public void FR9_D142_ConsecutiveSessionsPartitionTheDateLine_EachDateAppliesExactlyOnce()
    {
        // THE invariant, asserted as a property rather than as three examples. Walk every calendar date
        // across a gap-containing session line and count how many windows claim it: never zero (an
        // action silently skipped), never two (an action applied twice — a split compounding itself).
        string[] sessions = ["2026-07-10", "2026-07-13", "2026-07-14", "2026-07-17"];

        for (var day = new DateOnly(2026, 7, 11); day <= new DateOnly(2026, 7, 17); day = day.AddDays(1))
        {
            var date = day.ToString("yyyy-MM-dd");
            var claims = 0;
            for (var i = 1; i < sessions.Length; i++)
            {
                if (CorporateActionWindow.Contains(date, sessions[i - 1], sessions[i])) claims++;
            }

            Assert.Equal(1, claims);
        }
    }
}
