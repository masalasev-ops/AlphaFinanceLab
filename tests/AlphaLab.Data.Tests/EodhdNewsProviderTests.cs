using AlphaLab.Core.Llm;
using AlphaLab.Data.Providers;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The EODHD `/news` parse (INTEGRATIONS §1, VERIFIED 2026-07-13) against the documented shape, and the
/// post-budget store. Pure parse, no HTTP — the same discipline as the captured EODHD bar fixtures.
/// </summary>
public class EodhdNewsProviderTests
{
    private const string Payload = """
    [
      {"date":"2026-08-03T12:00:00+00:00","title":"Apple beats","content":"Body one",
       "link":"https://example.test/1","symbols":["AAPL.US"],"tags":["EARNINGS"],"sentiment":{"polarity":0.4}},
      {"date":"2026-08-03T09:00:00+00:00","title":"Fed holds","content":"Body two",
       "link":"https://example.test/2","symbols":[],"tags":["MACRO"],"sentiment":{"polarity":-0.1}}
    ]
    """;

    [Fact]
    public void Parse_ReadsTheDocumentedShape()
    {
        var items = EodhdNewsProvider.Parse(Payload);

        Assert.Equal(2, items.Count);
        Assert.Equal("Apple beats", items[0].Title);
        Assert.Equal("Body one", items[0].Content);
        Assert.Equal(["AAPL.US"], items[0].Symbols);
        Assert.Equal(["MACRO"], items[1].Tags);
        Assert.Empty(items[1].Symbols);
    }

    [Fact]
    public void Parse_IgnoresTheInlineSentiment_BecauseTheScoreIsRetired()
    {
        // EODHD returns `sentiment` inline per article. The machine-readable sentiment score is RETIRED
        // (D46 amended by D79–D82; golden rule 28) — reading it back in would resurrect exactly the thing
        // that was retired, and it would do so invisibly, because the field is free and already there.
        // NewsArticle has no sentiment member, so this is structural rather than a runtime check.
        var items = EodhdNewsProvider.Parse(Payload);

        Assert.DoesNotContain(
            typeof(NewsArticle).GetProperties(),
            p => p.Name.Contains("sentiment", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Parse_MalformedOrEmptyPayload_IsAnEmptyDay_NotAThrow()
    {
        // A no-news day and a shape change both degrade to "no read", never to an exception that would
        // take the daily pipeline down with it (D24).
        Assert.Empty(EodhdNewsProvider.Parse("[]"));
        Assert.Empty(EodhdNewsProvider.Parse("{}"));
    }

    [Fact]
    public void Parse_MissingOptionalFields_Default_RatherThanThrow()
    {
        var items = EodhdNewsProvider.Parse("""[{"title":"bare"}]""");

        Assert.Single(items);
        Assert.Equal("bare", items[0].Title);
        Assert.Equal("", items[0].Content);
        Assert.Empty(items[0].Symbols);
        Assert.Empty(items[0].Tags);
    }

    [Fact]
    public void NewsEndpoint_CostsFiveUnits_NotOne()
    {
        // INTEGRATIONS §1: /news is weighted 5 against the 100k/day cap. A flat per-call count would
        // under-report consumption and could pass a headroom check that should have failed.
        Assert.Equal(5, EodhdEndpointCost.For(EodhdEndpoint.News));
    }
}

/// <summary>The post-budget store (`news_items`) against real SQLite.</summary>
public class AdmittedNewsStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"alphalab-news-{Guid.NewGuid():N}.db");

    private AlphaLabDbContext Migrated()
    {
        var db = new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.Migrate();
        return db;
    }

    private static AdmittedArticle Admitted(string title, int truncated = 0) =>
        new(new NewsArticle(title, "body", "src", ["AAPL.US"], []), $"hash-{title}", truncated);

    [Fact]
    public async Task Save_PersistsAdmittedArticles_WithTheirTruncationCount()
    {
        using var db = Migrated();
        var store = new AdmittedNewsStore(db);

        await store.SaveAsync("2026-08-03", [Admitted("A", 1200), Admitted("B")]);

        var rows = await db.NewsItems.OrderBy(n => n.TitleHash).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1200, rows[0].TruncatedChars);
        Assert.Contains("AAPL.US", rows[0].SymbolsJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_IsIdempotent_SoAReRunAddsNothing()
    {
        // A re-run of a day must not duplicate. The store skips hashes already present; the unique index
        // is the backstop rather than the mechanism.
        using var db = Migrated();
        var store = new AdmittedNewsStore(db);

        await store.SaveAsync("2026-08-03", [Admitted("A"), Admitted("B")]);
        await store.SaveAsync("2026-08-03", [Admitted("A"), Admitted("B"), Admitted("C")]);

        Assert.Equal(3, await db.NewsItems.CountAsync());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
    }
}
