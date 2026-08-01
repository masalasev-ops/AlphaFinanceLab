using AlphaLab.Core.Llm;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The Phase-5 M7 stores against real SQLite: <c>analysis_cache</c> (FR-21), <c>llm_budget_log</c> (D24)
/// and the <c>news_items</c> dedupe key. The provider's USE of these is unit-tested in AlphaLab.Llm.Tests
/// against fakes; here the point is the storage contract those fakes stand in for.
/// </summary>
public class LlmStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"alphalab-llm-{Guid.NewGuid():N}.db");

    private AlphaLabDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private AlphaLabDbContext Migrated()
    {
        var db = NewContext();
        db.Database.Migrate();
        return db;
    }

    [Fact]
    public async Task AnalysisCache_RoundTrips_AndIsKeyedOnAllThreeParts()
    {
        using var db = Migrated();
        var store = new AnalysisCacheStore(db);
        var usage = new TokenUsage(100, 50, 0, 0, 0.001m);

        await store.PutAsync("h1", "claude-opus-5", "2026-08-03", AnalysisTask.RegimeBrief, "answer", usage);

        Assert.NotNull(await store.TryGetAsync("h1", "claude-opus-5", "2026-08-03"));
        // Each part of the key matters independently — a different prompt, a re-pinned model, or another
        // day must all MISS rather than serve an answer that was never produced for them.
        Assert.Null(await store.TryGetAsync("h2", "claude-opus-5", "2026-08-03"));
        Assert.Null(await store.TryGetAsync("h1", "claude-sonnet-4-6", "2026-08-03"));
        Assert.Null(await store.TryGetAsync("h1", "claude-opus-5", "2026-08-04"));
    }

    [Fact]
    public async Task AnalysisCache_PutIsIdempotent_SoAReRunNeitherThrowsNorDoubleCharges()
    {
        using var db = Migrated();
        var store = new AnalysisCacheStore(db);
        var usage = new TokenUsage(10, 5, 0, 0, 0.0001m);

        await store.PutAsync("h1", "m", "2026-08-03", AnalysisTask.Skeptic, "first", usage);
        await store.PutAsync("h1", "m", "2026-08-03", AnalysisTask.Skeptic, "second", usage);

        Assert.Equal(1, await db.AnalysisCache.CountAsync());
        var hit = await store.TryGetAsync("h1", "m", "2026-08-03");
        Assert.Equal("first", hit!.RawOutput);   // first write wins; the row is a record, not a scratchpad
    }

    [Fact]
    public async Task AnalysisCache_TaskCheckConstraint_RejectsAnUnknownValue_Finding319()
    {
        // Finding 319: the column enumerated its values in a comment only. The CHECK is written from
        // AnalysisTaskNames.All, so the constraint and the C# type cannot drift.
        using var db = Migrated();
        db.AnalysisCache.Add(new AnalysisCacheRow
        {
            PromptHash = "h", Model = "m", AsOf = "2026-08-03",
            Task = "not_a_task", OutputJson = "{}",
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("news_extraction")]
    [InlineData("regime_brief")]
    [InlineData("research_brief")]
    [InlineData("skeptic")]
    [InlineData("hypotheses")]
    public async Task AnalysisCache_TaskCheckConstraint_AdmitsEveryVocabularyValue(string task)
    {
        // The positive half: the constraint must admit exactly the five names the type defines. Without
        // this, a too-narrow CHECK would only surface when a task first ran in production.
        using var db = Migrated();
        db.AnalysisCache.Add(new AnalysisCacheRow
        {
            PromptHash = $"h-{task}", Model = "m", AsOf = "2026-08-03",
            Task = task, OutputJson = "{}",
        });

        await db.SaveChangesAsync();
        Assert.Equal(1, await db.AnalysisCache.CountAsync());
    }

    [Fact]
    public async Task LlmBudgetLog_AccumulatesInPlace_AndDegradedIsSticky()
    {
        using var db = Migrated();
        var ledger = new LlmBudgetLedger(db);

        await ledger.RecordAsync("2026-08-03", 1, new TokenUsage(100, 50, 0, 0, 0.01m), degraded: false, null);
        await ledger.RecordAsync("2026-08-03", 1, new TokenUsage(100, 50, 0, 0, 0.01m), degraded: true, "ceiling");
        await ledger.RecordAsync("2026-08-03", 1, new TokenUsage(100, 50, 0, 0, 0.01m), degraded: false, null);

        var state = await ledger.GetAsync("2026-08-03");
        Assert.Equal(3, state.Calls);
        Assert.Equal(450, state.Tokens);
        Assert.Equal(0.03m, state.CostUsd);

        // Sticky: a day on which anything was refused stays marked. The question the flag answers — "did
        // we see everything that day?" — cannot be un-answered by a later successful call.
        var row = await db.LlmBudgetLog.SingleAsync();
        Assert.Equal(1, row.Degraded);
    }

    [Fact]
    public async Task LlmBudgetLog_UnknownDay_IsEmpty_NotNull()
    {
        using var db = Migrated();
        Assert.Equal(BudgetState.Empty, await new LlmBudgetLedger(db).GetAsync("2026-01-01"));
    }

    [Fact]
    public async Task NewsItems_DuplicateTitleHashOnTheSameDay_IsRejectedByTheStore()
    {
        // The D46 title-hash dedupe made structural: even if the in-memory dedupe were bypassed, a
        // duplicate cannot be stored.
        using var db = Migrated();
        db.NewsItems.Add(new NewsItemRow { AsOf = "2026-08-03", TitleHash = "t1", Title = "A" });
        await db.SaveChangesAsync();

        db.NewsItems.Add(new NewsItemRow { AsOf = "2026-08-03", TitleHash = "t1", Title = "A (again)" });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task NewsItems_SameTitleOnADifferentDay_IsAdmitted()
    {
        // The dedupe is per-day: a recurring headline is new evidence on a new day.
        using var db = Migrated();
        db.NewsItems.Add(new NewsItemRow { AsOf = "2026-08-03", TitleHash = "t1" });
        db.NewsItems.Add(new NewsItemRow { AsOf = "2026-08-04", TitleHash = "t1" });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.NewsItems.CountAsync());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
    }
}
