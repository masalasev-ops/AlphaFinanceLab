using System.Text.Json;
using AlphaLab.Core.Json;
using AlphaLab.Core.ReadModels;

namespace AlphaLab.Core.Tests;

public class ReadModelStampTests
{
    [Fact]
    public void NoRunYet_SerializesWithSnakeCaseKeys_AndEmitsNulls()
    {
        var json = JsonSerializer.Serialize(ReadModelStamp.NoRunYet, AlphaLabJson.Options);

        Assert.Contains("\"status\":\"no_run_yet\"", json);
        // Nulls are emitted (the policy must NOT drop them — D66).
        Assert.Contains("\"run_id\":null", json);
        Assert.Contains("\"watermark\":null", json);
        Assert.Contains("\"as_of\":null", json);
        Assert.DoesNotContain("runId", json);
    }

    [Fact]
    public void Stamped_CarriesRunContext()
    {
        var stamp = ReadModelStamp.Stamped(7, "2026-01-02T00:00:00Z", "2026-01-02");

        Assert.Equal(ReadModelStampStatus.Stamped, stamp.Status);
        var json = JsonSerializer.Serialize(stamp, AlphaLabJson.Options);
        Assert.Contains("\"status\":\"stamped\"", json);
        Assert.Contains("\"run_id\":7", json);
    }

    [Fact]
    public void EmptyScreenReadModels_AreNoRunYet_WithNoRows()
    {
        Assert.Equal(ReadModelStampStatus.NoRunYet, StrategiesReadModel.NoRunYet.Stamp.Status);
        Assert.Empty(StrategiesReadModel.NoRunYet.Rows);
        Assert.True(ReplayReadModel.NoRunYet.Quarantined);
    }
}
