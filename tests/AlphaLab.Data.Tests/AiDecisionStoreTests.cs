using AlphaLab.Core.Llm;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The D81 persist-before-use seam and the D105 replay guarantee, against real SQLite.
/// </summary>
public class AiDecisionStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"alphalab-ai-{Guid.NewGuid():N}.db");

    private AlphaLabDbContext Migrated()
    {
        var db = new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.Migrate();
        return db;
    }

    private static AiDecisionRecord Decision(string output = "scores", string? applied = null) =>
        new("strat-1", "2026-08-03", "packhash-1", "pv-1", "claude-opus-5",
            output, new TokenUsage(100, 50, 0, 0, 0.0123m), applied, """{"effort":"high"}""");

    [Fact]
    public async Task FX_AiDecisionIsTheRow_ASecondCallReturnsTheStoredRow_NeverOverwrites()
    {
        // D81 rule 1. If a later call could overwrite, "the persisted output IS the decision" would hold
        // only until something called again — and reproduce-day would replay whichever call happened
        // last, not the one the day actually traded on.
        using var db = Migrated();
        var store = new AiDecisionStore(db);

        await store.PersistAsync(Decision("FIRST"));
        var second = await store.PersistAsync(Decision("SECOND"));

        Assert.Equal("FIRST", second.RawOutput);
        Assert.Equal(1, await db.AiDecisions.CountAsync());
    }

    [Fact]
    public async Task FX_AiDecisionIsTheRow_AStoredRowIsFound_SoAConsumerNeedNotCallTheModel()
    {
        // TryGetAsync existing at all IS the mechanism: a non-null result means "do not call the model".
        using var db = Migrated();
        var store = new AiDecisionStore(db);
        await store.PersistAsync(Decision());

        var found = await store.TryGetAsync("strat-1", "2026-08-03", "pv-1");

        Assert.NotNull(found);
        Assert.Equal("packhash-1", found!.PackHash);
        Assert.Equal(0.0123m, found.Usage.CostUsd);
    }

    [Fact]
    public async Task PromptVersionIsPartOfTheKey_SoAForkedPolicyDoesNotReuseAnOldDecision()
    {
        // The prompt text is a frozen param (D81 rule 2) — any change forks a candidate. A decision made
        // under the old prompt must not answer for the new one.
        using var db = Migrated();
        var store = new AiDecisionStore(db);
        await store.PersistAsync(Decision());

        Assert.Null(await store.TryGetAsync("strat-1", "2026-08-03", "pv-2"));
    }

    [Fact]
    public async Task ArtefactC_IsRecordedOnce_AndCannotBeRecordedForANonexistentDecision()
    {
        using var db = Migrated();
        var store = new AiDecisionStore(db);
        await store.PersistAsync(Decision());

        await store.RecordAppliedAsync("strat-1", "2026-08-03", "pv-1", """{"filled":3,"clamped":1}""");
        // A second application would mean the decision was consumed twice — itself the defect, so the
        // first record stands.
        await store.RecordAppliedAsync("strat-1", "2026-08-03", "pv-1", """{"filled":99}""");

        var row = await db.AiDecisions.SingleAsync();
        Assert.Contains("clamped", row.AppliedJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("99", row.AppliedJson!, StringComparison.Ordinal);

        // Recording an application of a decision that was never persisted would be recording an
        // application of nothing.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordAppliedAsync("ghost", "2026-08-03", "pv-1", "{}"));
    }

    [Fact]
    public async Task CostIsDecimalTextNotReal_SoTheRecordKeepsItsPrecision()
    {
        // D69: this row is part of a decision record a ledger claim can rest on, which is the line SCHEMA
        // draws between it and analysis_cache's REAL cost column.
        using var db = Migrated();
        var store = new AiDecisionStore(db);
        await store.PersistAsync(Decision() with { Usage = new TokenUsage(1, 1, 0, 0, 0.000000123456789m) });

        var round = await store.TryGetAsync("strat-1", "2026-08-03", "pv-1");
        Assert.Equal(0.000000123456789m, round!.Usage.CostUsd);
    }

    [Fact]
    public async Task AllFourArtefactsSurviveTheRoundTrip()
    {
        // §23.8.1: each catches a failure the others structurally cannot see, so a store that dropped one
        // would leave a class of defect permanently invisible.
        using var db = Migrated();
        var store = new AiDecisionStore(db);
        await store.PersistAsync(Decision(applied: """{"acted":true}"""));

        var r = (await store.TryGetAsync("strat-1", "2026-08-03", "pv-1"))!;

        Assert.Equal("packhash-1", r.PackHash);        // (a) what it saw
        Assert.Equal("scores", r.RawOutput);           // (b) what it said, raw
        Assert.Contains("acted", r.AppliedJson!, StringComparison.Ordinal);   // (c) what the arena did
        Assert.Equal("claude-opus-5", r.ModelVersion); // (d) which model
        Assert.Contains("effort", r.SamplingJson!, StringComparison.Ordinal); // (d) how it was configured
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // P20: process-global; safe ONLY because parallelization is disabled assembly-wide.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
    }
}
