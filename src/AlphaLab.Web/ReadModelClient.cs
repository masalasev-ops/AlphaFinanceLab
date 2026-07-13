using System.Net.Http.Json;
using AlphaLab.Core.Json;
using AlphaLab.Core.ReadModels;

namespace AlphaLab.Web;

/// <summary>Result of probing a screen endpoint: the stamp (if reached) or a transport error message.</summary>
public sealed record ScreenState(ReadModelStamp? Stamp, string? Error)
{
    public bool Reached => Error is null;
    public bool NoRunYet => Stamp?.Status == ReadModelStampStatus.NoRunYet;
}

/// <summary>
/// The client's ONLY door to the system (D57): it talks HTTP to AlphaLab.Api using the shared
/// snake_case JSON contract. It computes no thresholds and resolves no honesty rules — those already
/// live in the read-models it receives (D58). Phase 0 reads only each read-model's stamp.
/// </summary>
public sealed class ReadModelClient(HttpClient http)
{
    private sealed record StampCarrier(ReadModelStamp Stamp);

    public async Task<ScreenState> GetScreenStateAsync(string apiPath, CancellationToken ct = default)
    {
        try
        {
            var carrier = await http.GetFromJsonAsync<StampCarrier>(apiPath, AlphaLabJson.Options, ct);
            return new ScreenState(carrier?.Stamp ?? ReadModelStamp.NoRunYet, Error: null);
        }
        catch (Exception ex)
        {
            return new ScreenState(Stamp: null, Error: ex.Message);
        }
    }
}
