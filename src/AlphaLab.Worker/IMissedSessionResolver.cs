using AlphaLab.Data;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker;

/// <summary>
/// Computes the trading sessions the Worker missed and must replay in order (D47/D54). In Phase 0
/// there is no <c>trading_calendar</c> table and zero committed runs, so this always resolves to
/// "nothing to do" — the real calendar-driven logic arrives with the staged pipeline in Phase 2.
/// </summary>
public interface IMissedSessionResolver
{
    Task<IReadOnlyList<DateOnly>> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Phase-0 resolver: with no calendar and no committed runs, there is nothing to catch up. Reading the
/// runs count also proves the schema is present and the DB opens (SchemaStartup ran first).
/// </summary>
public sealed class Phase0MissedSessionResolver(AlphaLabDbContext db) : IMissedSessionResolver
{
    public async Task<IReadOnlyList<DateOnly>> ResolveAsync(CancellationToken cancellationToken = default)
    {
        // No trading_calendar (Phase 1) and no runs yet => no missed sessions. Reading the runs count
        // also proves the schema is present and the DB opens.
        _ = await db.Runs.CountAsync(cancellationToken); // always 0 in Phase 0; referenced so intent is explicit.
        return [];
    }
}
