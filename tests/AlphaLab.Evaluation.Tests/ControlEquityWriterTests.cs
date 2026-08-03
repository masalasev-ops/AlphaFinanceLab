using AlphaLab.Evaluation.Populations;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// finding 359: the prior-equity seed is a SEEK of one session, not a scan of the whole run.
///
/// <c>LatestEquity</c> asked for every row older than the target day and grouped it down to the newest per
/// member, so a replay day's cost grew with the run behind it (42.5% of sampled stacks by session 1,178,
/// scanning ~242k rows per population per session). <c>EquityAt</c> answers the same question with one
/// seek — legitimate because a day's rows are written by ONE AddRange inside that day's write transaction
/// and are therefore all-or-nothing.
///
/// These tests pin BOTH halves of that claim: the fast path must agree with the scan on a contiguous
/// history, and it must return empty — never a wrong answer — when the prior session is missing, because
/// empty is what makes the caller fall back to the scan instead of resetting members to inception.
/// </summary>
public class ControlEquityWriterTests
{
    private const long PopulationId = 1;
    private const string Day1 = "2024-01-02";
    private const string Day2 = "2024-01-03";
    private const string Day3 = "2024-01-04";

    [Fact]
    public void Finding359_EquityAt_MatchesLatestEquity_OnAContiguousHistory()
    {
        using var arena = new EvalArena();

        Write(arena, Day1, [100m, 200m, 300m]);
        Write(arena, Day2, [110m, 210m, 310m]);

        using var db = arena.Open();
        var writer = new ControlEquityWriter(db);

        var scan = writer.LatestEquity(PopulationId, Day3);
        var seek = writer.EquityAt(PopulationId, Day2);

        // The whole optimisation is this equality. If it ever fails the fast path is simply wrong.
        Assert.Equal(scan.OrderBy(kv => kv.Key), seek.OrderBy(kv => kv.Key));
        Assert.Equal([110m, 210m, 310m], seek.OrderBy(kv => kv.Key).Select(kv => kv.Value));
    }

    [Fact]
    public void Finding359_EquityAt_ReturnsEmpty_WhenThePriorSessionHasNoRows_SoTheCallerFallsBack()
    {
        using var arena = new EvalArena();

        // A gap: Day1 has equity, Day2 was never written.
        Write(arena, Day1, [100m, 200m, 300m]);

        using var db = arena.Open();
        var writer = new ControlEquityWriter(db);

        // Empty is the FALLBACK SIGNAL, not an answer. Were the caller to read it as inception it would
        // silently reset every member to starting cash — the fail-open this test exists to forbid.
        Assert.Empty(writer.EquityAt(PopulationId, Day2));

        // ...and the scan the caller falls back to still carries Day1's equity forward (rule 10).
        var carried = writer.LatestEquity(PopulationId, Day3);
        Assert.Equal([100m, 200m, 300m], carried.OrderBy(kv => kv.Key).Select(kv => kv.Value));
    }

    [Fact]
    public void Finding359_EquityAt_IsScopedToItsPopulationAndRunKind()
    {
        using var arena = new EvalArena();

        Write(arena, Day1, [100m, 200m], populationId: 1);
        Write(arena, Day1, [900m, 900m], populationId: 2);
        Write(arena, Day1, [500m, 500m], populationId: 1, runKind: "replay");

        using var db = arena.Open();
        var writer = new ControlEquityWriter(db);

        Assert.Equal([100m, 200m], writer.EquityAt(1, Day1).OrderBy(kv => kv.Key).Select(kv => kv.Value));
        Assert.Equal([900m, 900m], writer.EquityAt(2, Day1).OrderBy(kv => kv.Key).Select(kv => kv.Value));
        Assert.Equal([500m, 500m], writer.EquityAt(1, Day1, "replay").OrderBy(kv => kv.Key).Select(kv => kv.Value));
    }

    private static void Write(EvalArena arena, string asOf, decimal[] equities,
        long populationId = PopulationId, string runKind = "live")
    {
        using var db = arena.Open();
        var points = equities
            .Select((e, i) => new ControlEquityWriter.Point(populationId, i, e))
            .ToList();
        new ControlEquityWriter(db).Write(asOf, points, runKind);
    }
}
