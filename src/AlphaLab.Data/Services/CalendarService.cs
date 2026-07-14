using System.Globalization;

namespace AlphaLab.Data.Services;

/// <summary>
/// Reads the seeded NYSE trading calendar (FR-30 / D54). Consumed by the orchestrator trigger, catch-up
/// (<see cref="SessionsBetween"/> is the missed-session computation), T/T+1 pairing, and warm-up counting.
/// Dates are ISO-8601 (yyyy-MM-dd) TEXT in <c>trading_calendar</c> and sort chronologically as an ordinal
/// string compare. The ordered session snapshot is loaded once per instance and binary-searched, so the
/// service never fabricates a session — a non-session date simply isn't in the table.
/// </summary>
public interface ICalendarService
{
    /// <summary>True iff <paramref name="date"/> is a seeded NYSE session (full or half).</summary>
    bool IsTradingDay(DateOnly date);

    /// <summary>The latest session strictly before <paramref name="date"/>, or null if none is seeded.</summary>
    DateOnly? PreviousSession(DateOnly date);

    /// <summary>The earliest session strictly after <paramref name="date"/>, or null if none is seeded.</summary>
    DateOnly? NextSession(DateOnly date);

    /// <summary>Every session in [from, to] inclusive, ordered — the catch-up "sessions I missed" set.
    /// Only real sessions are returned; holidays and weekends in the range are never fabricated.</summary>
    IReadOnlyList<DateOnly> SessionsBetween(DateOnly from, DateOnly to);

    /// <summary>The ET close time ("16:00" full, "13:00" half) for a session, or null if not a session.</summary>
    string? CloseTime(DateOnly date);

    /// <summary>True iff <paramref name="date"/> is a half-day (13:00 ET) session.</summary>
    bool IsHalfDay(DateOnly date);
}

public sealed class CalendarService(AlphaLabDbContext db) : ICalendarService
{
    private string[]? _dates;                                   // ordered ISO session dates (ordinal == chronological)
    private Dictionary<string, string>? _closeByDate;           // ISO date -> close_time_local

    private void EnsureLoaded()
    {
        if (_dates is not null) return;
        var rows = db.TradingCalendar.OrderBy(r => r.Date).ToList();
        _dates = rows.Select(r => r.Date).ToArray();
        _closeByDate = rows.ToDictionary(r => r.Date, r => r.CloseTimeLocal, StringComparer.Ordinal);
    }

    public bool IsTradingDay(DateOnly date)
    {
        EnsureLoaded();
        return _closeByDate!.ContainsKey(Iso(date));
    }

    public string? CloseTime(DateOnly date)
    {
        EnsureLoaded();
        return _closeByDate!.TryGetValue(Iso(date), out var close) ? close : null;
    }

    public bool IsHalfDay(DateOnly date) => CloseTime(date) == "13:00";

    public DateOnly? PreviousSession(DateOnly date)
    {
        EnsureLoaded();
        var idx = FloorIndex(Iso(date), inclusive: false); // largest index with date < target
        return idx >= 0 ? Parse(_dates![idx]) : null;
    }

    public DateOnly? NextSession(DateOnly date)
    {
        EnsureLoaded();
        var idx = CeilingIndex(Iso(date), inclusive: false); // smallest index with date > target
        return idx < _dates!.Length ? Parse(_dates![idx]) : null;
    }

    public IReadOnlyList<DateOnly> SessionsBetween(DateOnly from, DateOnly to)
    {
        EnsureLoaded();
        if (to < from) return [];
        var start = CeilingIndex(Iso(from), inclusive: true);  // first index with date >= from
        var end = FloorIndex(Iso(to), inclusive: true);        // last index with date <= to
        if (start > end) return [];

        var result = new List<DateOnly>(end - start + 1);
        for (var i = start; i <= end; i++) result.Add(Parse(_dates![i]));
        return result;
    }

    // Largest index whose date is < target (inclusive:false) or <= target (inclusive:true); -1 if none.
    private int FloorIndex(string target, bool inclusive)
    {
        var i = Array.BinarySearch(_dates!, target, StringComparer.Ordinal);
        if (i >= 0) return inclusive ? i : i - 1;
        return ~i - 1; // ~i is the first index > target; the one before it is the last < target
    }

    // Smallest index whose date is > target (inclusive:false) or >= target (inclusive:true); Length if none.
    private int CeilingIndex(string target, bool inclusive)
    {
        var i = Array.BinarySearch(_dates!, target, StringComparer.Ordinal);
        if (i >= 0) return inclusive ? i : i + 1;
        return ~i; // ~i is the first index > target (also the first >= target when target is absent)
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly Parse(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
