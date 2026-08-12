using AlphaLab.Data;
using AlphaLab.Data.Http;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;

namespace AlphaLab.Worker;

/// <summary>
/// Which membership providers the FORWARD refresh runs for a given universe (D154, finding 426).
///
/// <para>**WHY THIS IS A CLASS AND NOT FOUR LINES IN `Program.cs`.** It used to be an inline factory under
/// a comment claiming the pair was *"UNIVERSE-DRIVEN so the rule-22 widen stays a config flip: sp500
/// selects IVV + the S&amp;P 500 cross-check. An unknown universe registers NOTHING rather than guessing a
/// provider"*. Every clause of that was false: the primary was <see cref="ISharesHoldingsOptions.Oef"/>
/// unconditionally, the cross-check URL was the S&amp;P 100 page unconditionally, there was no branch of
/// any kind, and the factory could not return null. `Program.cs` is top-level statements in an exe with no
/// test seam, so the claim was not merely wrong — it was unfalsifiable where it stood. Moving the decision
/// into a pure function is what lets a test assert it.</para>
///
/// <para>**THE sp500 ARM IS NOT WIRED, AND SAYING SO IS THE POINT.** Rule 22 gates the widen on Phase 4
/// sign-off plus a backfill delta, and `docs/REBUILD.md` already records `Ivv()` as *"the S&amp;P 500
/// widening mechanism — a recorded proposal, not a flag you flip today"*. So an sp500 flip returns
/// <c>null</c> here: nothing is registered, <see cref="MembershipRefreshStep"/> takes its
/// "no provider composition" branch, and the roster's freshness goes visibly stale. That is the behaviour
/// the old comment described, now actually produced — and the branch that documented it, previously
/// unreachable in every composition that exists, becomes reachable.</para>
///
/// <para>**WHY NOT THROW,** given `BackfillRunner` refuses an sp500 bootstrap outright: the CLI is a
/// one-shot operator command where an early, loud failure costs nothing, whereas this runs inside the
/// daily Worker. A throw at composition time would take the whole daily pipeline down over a roster
/// refresh — trading the D53 run for a data-freshness feature. Stale-and-visible is the proportionate
/// failure, and it is the one the code around it was already written to expect.</para>
/// </summary>
public static class MembershipComposition
{
    /// <summary>The one universe whose forward membership providers are actually wired.</summary>
    public const string WiredUniverse = "sp100";

    /// <summary>The Wikipedia S&amp;P 100 cross-check page (INTEGRATIONS). Used when config omits it.</summary>
    public const string DefaultSp100CrossCheckUrl = "https://en.wikipedia.org/wiki/S%26P_100";

    /// <summary>
    /// Does this build have forward membership providers wired for <paramref name="universe"/>? The
    /// registration-time question, kept separate from <see cref="TryCreate"/> so `Program.cs` can decide
    /// whether to register the service AT ALL without first constructing a DbContext and an HTTP client
    /// it may not need. Both answer from the same field, so they cannot drift.
    /// </summary>
    public static bool IsWired(UniverseOptions universe)
    {
        ArgumentNullException.ThrowIfNull(universe);
        return string.Equals(universe.Bootstrap.Universe, WiredUniverse, StringComparison.Ordinal);
    }

    /// <summary>
    /// The forward membership refresh for <paramref name="universe"/>, or <c>null</c> when this build has
    /// no providers wired for it — in which case the caller must register nothing at all, so that
    /// <c>GetService&lt;MembershipRefresh&gt;()</c> returns null and the step's own guard fires.
    /// </summary>
    /// <remarks>
    /// WHAT THE sp500 ARM STILL NEEDS, listed here rather than in a comment somewhere else, because this
    /// is the function that would gain the branch:
    /// <list type="number">
    /// <item>a <c>Backfill:WikipediaSp500Url</c> config key — it exists nowhere today, and CONFIG_REFERENCE
    /// is the sole source of truth for keys, so adding it is a documented change and not a literal;</item>
    /// <item>evidence that <see cref="WikipediaMembershipCrossCheck"/> parses the S&amp;P 500 table at all —
    /// its only fixture is the S&amp;P 100 page, so this is unverified rather than assumed working;</item>
    /// <item>the cross-check <c>Source</c> label must come from <c>UniverseOptions.MembershipCrossCheck</c>,
    /// NOT <c>Bootstrap.MembershipCrossCheck</c> — a naive flip would keep stamping S&amp;P 500 rows
    /// <c>wikipedia_sp100</c> in `index_membership_log.source`, which is false provenance (D137) and worse
    /// than the honest absence above.</item>
    /// </list>
    /// </remarks>
    public static MembershipRefresh? TryCreate(
        UniverseOptions universe,
        AlphaLabDbContext db,
        IResilientHttpClient http,
        IRawCache? raw,
        string? crossCheckUrl)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(http);

        if (!IsWired(universe)) return null;

        return new MembershipRefresh(
            db,
            new ISharesHoldingsMembershipProvider(http, ISharesHoldingsOptions.Oef(), raw),
            new WikipediaMembershipCrossCheck(http, new WikipediaMembershipOptions
            {
                Url = string.IsNullOrWhiteSpace(crossCheckUrl) ? DefaultSp100CrossCheckUrl : crossCheckUrl,
                Source = universe.Bootstrap.MembershipCrossCheck,
            }, raw));
    }
}
