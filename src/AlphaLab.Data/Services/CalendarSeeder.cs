using AlphaLab.Data.Providers;

namespace AlphaLab.Data.Services;

/// <summary>
/// Seeds <c>trading_calendar</c> from the pure <see cref="NyseCalendar"/> generator (FR-30 / D54,
/// MASTER §20.5 "seeded at setup ±30 years"). Idempotent: a date already present is left untouched (the
/// generator is deterministic, so a re-seed adds only genuinely new years and never rewrites history).
/// The live ±30y seed run is invoked by the backfill CLI (1.10, like the other provider writers); this
/// service is the reusable write path it calls.
/// </summary>
public interface ICalendarSeeder
{
    /// <summary>Generate [fromYear, toYear] and insert any sessions not already seeded. Returns the
    /// number of new rows written (0 on a full re-seed).</summary>
    int Seed(int fromYear, int toYear);
}

public sealed class CalendarSeeder(AlphaLabDbContext db) : ICalendarSeeder
{
    public int Seed(int fromYear, int toYear)
    {
        var generated = NyseCalendar.Generate(fromYear, toYear);
        var existing = db.TradingCalendar.Select(r => r.Date).ToHashSet(StringComparer.Ordinal);

        var inserted = 0;
        foreach (var row in generated)
        {
            if (existing.Add(row.Date)) // Add returns false if the date was already seeded
            {
                db.TradingCalendar.Add(row);
                inserted++;
            }
        }

        db.SaveChanges();
        return inserted;
    }
}
