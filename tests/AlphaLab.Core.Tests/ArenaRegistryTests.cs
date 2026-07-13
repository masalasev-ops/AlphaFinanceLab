using AlphaLab.Core.Arenas;

namespace AlphaLab.Core.Tests;

public class ArenaRegistryTests
{
    [Fact]
    public void FR37_ArenaRegistry_DrivesClientBaseUrl()
    {
        var registry = ArenaRegistry.FromEntries(
        [
            new ArenaEntry { Id = "sp500", DisplayName = "S&P 500", BaseUrl = "http://127.0.0.1:5230" },
            new ArenaEntry { Id = "sp1500", DisplayName = "S&P 1500", BaseUrl = "http://127.0.0.1:5231" },
        ]);

        // The active arena defaults to the first entry, and its baseUrl is what the client targets.
        Assert.Equal("sp500", registry.Active.Id);
        Assert.Equal("http://127.0.0.1:5230", registry.Active.BaseUrl);
        Assert.Equal(2, registry.Arenas.Count);
        Assert.False(registry.IsFallback);
    }

    [Fact]
    public void FromEntries_Empty_IsFlaggedFallback_ForFailClosedBanner()
    {
        var registry = ArenaRegistry.FromEntries(null, "http://localhost:5210/");

        Assert.Empty(registry.Arenas);
        Assert.Equal("http://localhost:5210/", registry.Active.BaseUrl);
        // P0-6 / hard rule 10: a missing registry is a config error the UI must surface, not a
        // working arena — the flag drives the config-error banner instead of silent bad requests.
        Assert.True(registry.IsFallback);
    }
}
