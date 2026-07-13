using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>
/// Pure headroom arithmetic (INTEGRATIONS §1: "the daily job must fit with ≥50% headroom, logged
/// to api_usage_log"). "≥50% headroom" means the run consumes at most (1 − fraction) of the plan
/// limit — i.e. calls ≤ 50% of the limit at the default fraction.
/// </summary>
public static class ApiUsageHeadroom
{
    public const double DefaultMinHeadroomFraction = 0.5;

    /// <summary>True iff <paramref name="calls"/> leaves at least <paramref name="minHeadroomFraction"/>
    /// of <paramref name="planLimit"/> unused. A non-positive limit ⇒ unknown ⇒ false (fail closed).</summary>
    public static bool HasHeadroom(int calls, int planLimit, double minHeadroomFraction = DefaultMinHeadroomFraction)
    {
        if (planLimit <= 0) return false;
        return calls <= (1.0 - minHeadroomFraction) * planLimit;
    }
}

/// <summary>Records per-day, per-source API call counts to api_usage_log (PK (as_of, source)).</summary>
public interface IApiUsageLog
{
    /// <summary>Upsert the call count for (asOf, source). Does NOT SaveChanges — the caller owns the
    /// transaction (so a backfill can fold this into its write). Returns the tracked row.</summary>
    ApiUsageLogRow Record(string asOf, string source, int calls, int? planLimit);
}

/// <summary>EF-backed <see cref="IApiUsageLog"/>. Upsert (not append-only — this is a counter, not a bar).</summary>
public sealed class ApiUsageLogWriter(AlphaLabDbContext db) : IApiUsageLog
{
    public ApiUsageLogRow Record(string asOf, string source, int calls, int? planLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var row = db.ApiUsageLog.Find(asOf, source);
        if (row is null)
        {
            row = new ApiUsageLogRow { AsOf = asOf, Source = source, Calls = calls, PlanLimit = planLimit };
            db.ApiUsageLog.Add(row);
        }
        else
        {
            row.Calls = calls;
            row.PlanLimit = planLimit ?? row.PlanLimit;
        }
        return row;
    }
}
