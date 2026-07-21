using AlphaLab.Core.ReadModels;
using AlphaLab.Data;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>Builds the D66 read-model stamp from the latest committed FORWARD run (never replay — a
/// forward read-model is stamped from a forward run by construction, FR-33).</summary>
internal static class ReadModelStamps
{
    public static ReadModelStamp LatestForward(AlphaLabDbContext db)
    {
        var run = db.Runs
            .Where(r => r.Status == "ok" && (r.RunKind == "live" || r.RunKind == "catchup"))
            .OrderByDescending(r => r.AsOf).ThenByDescending(r => r.RunId)
            .FirstOrDefault();
        return run is null ? ReadModelStamp.NoRunYet : ReadModelStamp.Stamped(run.RunId, run.Watermark, run.AsOf);
    }
}
