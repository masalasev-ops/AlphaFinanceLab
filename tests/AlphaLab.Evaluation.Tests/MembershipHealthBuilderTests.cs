using AlphaLab.Core.ReadModels;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.ReadModels;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// FX-MembershipHealth — finding 197's third deliverable, as a read-model (D58 / rule 18): the Data-health
/// freshness reading must show membership's TRUE FETCH DATE, not the run date.
///
/// The state machine has THREE states and the third is the load-bearing one. A two-state model would
/// render every pre-M11 row — deliberately un-backfilled, because the only value available was the run
/// date — as STALE, and an operator would open the lab on day one and chase a divergence that does not
/// exist. Unprovenanced is not the same as old.
/// </summary>
public class MembershipHealthBuilderTests
{
    private static void Log(EvalArena arena, string asOf, int agreed, string? observedAt, string? source, string? note = null)
    {
        using var db = arena.Open();
        db.IndexMembershipLog.Add(new IndexMembershipLogRow
        {
            AsOf = asOf, SourceCount = 101, CrosscheckCount = 101, Agreed = agreed,
            AddsJson = agreed == 1 ? "[]" : null, DropsJson = agreed == 1 ? "[]" : null,
            Note = note, ObservedAt = observedAt, Source = source,
        });
        db.SaveChanges();
    }

    [Fact]
    public void FX_MembershipHealth_NoReconcileYet_IsUnknown_AndSaysSo()
    {
        using var arena = new EvalArena();
        using var db = arena.Open();

        var health = MembershipHealthBuilder.Build(db, "2026-08-07");

        Assert.Equal(MembershipHealthState.Unknown, health.State);
        Assert.Null(health.FetchedAt);
        Assert.Contains("fresh store", health.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FX_MembershipHealth_FreshAndAgreeing_ShowsTheFetchInstant_NotTheRunDate()
    {
        using var arena = new EvalArena();
        // The run date and the fetch instant DIFFER — which is the entire point of finding 197. A cell
        // that showed as_of would read 2026-08-05 here and be wrong by two days.
        Log(arena, asOf: "2026-08-05", agreed: 1, observedAt: "2026-08-06T21:14:03Z", source: "oef_csv");

        using var db = arena.Open();
        var health = MembershipHealthBuilder.Build(db, "2026-08-07");

        Assert.Equal(MembershipHealthState.FreshAndAgreeing, health.State);
        Assert.Equal("2026-08-06T21:14:03Z", health.FetchedAt);
        Assert.Equal("2026-08-05", health.LastReconcileAsOf);   // kept BESIDE, not instead of
        Assert.NotEqual(health.FetchedAt, health.LastReconcileAsOf);
        Assert.Equal("oef_csv", health.Source);
        Assert.True(health.LastValidationAgreed);
        Assert.Null(health.HeldReason);
    }

    /// <summary>
    /// THE STATE THE PROVENANCE RULING REQUIRES. A pre-M11 row agreed, but carries no fetch instant —
    /// so freshness is UNJUDGEABLE, not bad. Rendering it stale would be the phantom divergence.
    /// </summary>
    [Fact]
    public void FX_MembershipHealth_ARowWithNoObservedAt_IsUnknown_NeverStale()
    {
        using var arena = new EvalArena();
        Log(arena, asOf: "2026-07-19", agreed: 1, observedAt: null, source: null);   // the live arena's shape

        using var db = arena.Open();
        var health = MembershipHealthBuilder.Build(db, "2026-08-07");   // 19 days later

        Assert.Equal(MembershipHealthState.Unknown, health.State);
        Assert.NotEqual(MembershipHealthState.StaleOrDiverging, health.State);
        Assert.Null(health.FetchedAt);
        Assert.True(health.LastValidationAgreed);           // it DID agree — that much is known
        Assert.Contains("NOT backfilled", health.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FX_MembershipHealth_AnAgedFetch_ReadsStale_WithItsAgeAndBudget()
    {
        using var arena = new EvalArena();
        Log(arena, asOf: "2026-07-19", agreed: 1, observedAt: "2026-07-19T22:00:00Z", source: "oef_csv");

        using var db = arena.Open();
        var health = MembershipHealthBuilder.Build(db, "2026-08-07");

        Assert.Equal(MembershipHealthState.StaleOrDiverging, health.State);
        Assert.True(health.LastValidationAgreed);   // agreeing but old — the two facts stay separate
        Assert.Contains("19 days ago", health.Reason, StringComparison.Ordinal);
    }

    /// <summary>A just-fetched divergence is the loudest case there is; freshness must not mask it.</summary>
    [Fact]
    public void FX_MembershipHealth_AHeldReconcile_ReadsDiverging_EvenWhenTheFetchIsFresh()
    {
        using var arena = new EvalArena();
        Log(arena, asOf: "2026-08-07", agreed: 0, observedAt: "2026-08-07T21:00:00Z", source: "oef_csv",
            note: "divergence: only-in-primary=[NEWCO], only-in-crosscheck=[OLDCO]");

        using var db = arena.Open();
        var health = MembershipHealthBuilder.Build(db, "2026-08-07");

        Assert.Equal(MembershipHealthState.StaleOrDiverging, health.State);
        Assert.False(health.LastValidationAgreed);
        Assert.Contains("NEWCO", health.HeldReason!, StringComparison.Ordinal);
        Assert.Contains("stored roster stands", health.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FX_MembershipHealth_ReadsTheLatestRow_NotTheFirst()
    {
        using var arena = new EvalArena();
        Log(arena, asOf: "2026-07-15", agreed: 1, observedAt: null, source: null);
        Log(arena, asOf: "2026-08-07", agreed: 1, observedAt: "2026-08-07T21:00:00Z", source: "oef_csv");

        using var db = arena.Open();
        var health = MembershipHealthBuilder.Build(db, "2026-08-07");

        Assert.Equal(MembershipHealthState.FreshAndAgreeing, health.State);
        Assert.Equal("2026-08-07T21:00:00Z", health.FetchedAt);
    }

    /// <summary>Every state carries a reason — including the ones that report an absence. UX-17's rule:
    /// a missing reading renders its reason inline, never an empty cell.</summary>
    [Fact]
    public void FX_MembershipHealth_EveryStateCarriesANonEmptyReason()
    {
        using var arena = new EvalArena();
        using (var db = arena.Open()) Assert.False(string.IsNullOrWhiteSpace(MembershipHealthBuilder.Build(db, "2026-08-07").Reason));

        Log(arena, asOf: "2026-08-07", agreed: 1, observedAt: "2026-08-07T21:00:00Z", source: "oef_csv");
        using (var db = arena.Open()) Assert.False(string.IsNullOrWhiteSpace(MembershipHealthBuilder.Build(db, "2026-08-07").Reason));

        Log(arena, asOf: "2026-08-07", agreed: 0, observedAt: "2026-08-07T21:00:00Z", source: "oef_csv", note: "count sanity breach");
        using (var db = arena.Open()) Assert.False(string.IsNullOrWhiteSpace(MembershipHealthBuilder.Build(db, "2026-08-07").Reason));
    }
}
