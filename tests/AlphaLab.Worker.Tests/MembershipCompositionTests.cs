using AlphaLab.Data;
using AlphaLab.Data.Http;
using AlphaLab.Worker;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// D154 / finding 426 — the forward membership composition, which used to be an untestable inline factory
/// in `Program.cs` under a comment that described a branch it did not have.
///
/// <para>The comment claimed the pair was *"UNIVERSE-DRIVEN so the rule-22 widen stays a config flip: sp500
/// selects IVV + the S&amp;P 500 cross-check. An unknown universe registers NOTHING rather than guessing a
/// provider"*. The factory called <c>Oef()</c> unconditionally, hardcoded the S&amp;P 100 page, had no
/// branch, and could not return null. **There were no tests for any of it** — none for the factory, none
/// for `MembershipRefreshStep`, none for `CatchupRunner`'s membership call. That is what made it a claim
/// rather than a behaviour: `Program.cs` is top-level statements in an exe with no test seam, so nothing
/// could have contradicted it where it stood.</para>
///
/// <para>No migrated store is needed: <see cref="MembershipComposition.TryCreate"/> CONSTRUCTS providers
/// and never queries, so an unopened in-memory context is the honest fixture — a real one would suggest
/// the function touches the store.</para>
/// </summary>
public class MembershipCompositionTests
{
    private static UniverseOptions For(string universe) => new() { Bootstrap = { Universe = universe } };

    private static AlphaLabDbContext Db() => new(
        new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite("Data Source=:memory:").Options);

    [Fact]
    public void D154_TheWiredUniverseComposesTheOefPlusWikipediaPair()
    {
        // The positive arm, and the control for every refusal below: sp100 IS wired, and stays wired.
        Assert.True(MembershipComposition.IsWired(For("sp100")));

        using var db = Db();
        Assert.NotNull(MembershipComposition.TryCreate(
            For("sp100"), db, new FakeHttp(), raw: null, crossCheckUrl: null));
    }

    [Theory]
    [InlineData("sp500")]     // the rule-22 widen — a recorded proposal, NOT a flag you flip today
    [InlineData("sp1500")]
    [InlineData("russell3000")]
    [InlineData("")]
    [InlineData("SP100")]     // case matters: the comparison is Ordinal, like CountBandFor's
    public void D154_AnUnwiredUniverseComposesNOTHING_RatherThanGuessingAProvider(string universe)
    {
        // THE BRANCH THE COMMENT CLAIMED, now actually produced. Before this, an sp500 flip would have
        // fetched ~101 OEF names, labelled them wikipedia_sp100, and handed them to the [495,510] count
        // band — failing closed at the count-sanity gate with a DATA-shaped error for an UNWIRED-CODE
        // cause. That is the exact failure BackfillRunner was fixed to prevent on the CLI side.
        Assert.False(MembershipComposition.IsWired(For(universe)));

        using var db = Db();
        Assert.Null(MembershipComposition.TryCreate(
            For(universe), db, new FakeHttp(), raw: null, crossCheckUrl: null));
    }

    [Fact]
    public void D154_IsWiredAndTryCreateAnswerFromTheSameField_SoTheyCannotDrift()
    {
        // Program.cs asks IsWired at REGISTRATION time (so it need not build a DbContext and an HTTP
        // client for a universe it will not serve) and TryCreate at RESOLVE time. Two predicates for one
        // question is how the original defect was shaped — a comment and a factory disagreeing — so this
        // pins that they agree on every value, including the ones nobody has thought of.
        using var db = Db();
        foreach (var universe in new[] { "sp100", "sp500", "sp1500", "", "nonsense", "SP100", "sp100 " })
        {
            var wired = MembershipComposition.IsWired(For(universe));
            var composed = MembershipComposition.TryCreate(
                For(universe), db, new FakeHttp(), raw: null, crossCheckUrl: null) is not null;
            Assert.Equal(wired, composed);
        }
    }

    [Fact]
    public void D154_TheWiredUniverseIsTheOneTheCountBandTreatsAsTheSlice()
    {
        // The band and the providers must agree about which universe is the launch slice, because
        // MembershipRefreshStep passes CountBandFor(token) alongside the fetched roster. If they ever
        // disagreed, the roster would be judged against the wrong [min,max] — which is precisely the
        // failure an unwired sp500 flip would have produced, one layer down.
        var options = For(MembershipComposition.WiredUniverse);
        var sliceBand = options.CountBandFor(MembershipComposition.WiredUniverse);
        var wideBand = options.CountBandFor("sp500");

        Assert.NotEqual(sliceBand, wideBand);                    // the band really does flip on the token
        Assert.Equal(options.Bootstrap.CountSanity, sliceBand);  // and the wired universe gets the SLICE band
    }

    /// <summary>A never-called HTTP client: <see cref="MembershipComposition.TryCreate"/> only constructs
    /// providers, so no request is issued and the test needs no transport. It THROWS rather than returning
    /// a stub, so a future TryCreate that started fetching would fail here instead of silently reaching
    /// the network from a composition function.</summary>
    private sealed class FakeHttp : IResilientHttpClient
    {
        public Task<string> GetStringAsync(string url, string source, CancellationToken ct = default)
            => throw new NotSupportedException("TryCreate must not issue a request.");
    }
}
