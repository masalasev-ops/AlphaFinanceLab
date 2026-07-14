namespace AlphaLab.Data.Services;

/// <summary>Point-in-time membership reads over the <c>index_membership</c> intervals (FR-4). The
/// foundation for FX-AsOfMembership: given a date, resolve the roster that was in the index that day.
/// Intervals are half-open <c>[added_on, removed_on)</c> (added inclusive, removed exclusive).</summary>
public interface IIndexMembershipRead
{
    /// <summary>The security_ids that were members of the index as of <paramref name="date"/>.</summary>
    IReadOnlyList<long> MembersAsOf(string date);
}

/// <summary>EF-backed <see cref="IIndexMembershipRead"/>. Rows are pulled into memory and range-filtered
/// with ordinal string compare (ISO-8601 sorts chronologically) — the same pattern as
/// <see cref="BarReadService"/> / <see cref="SecurityMaster"/>, avoiding EF's string.Compare translation.</summary>
public sealed class IndexMembershipReadService(AlphaLabDbContext db) : IIndexMembershipRead
{
    public IReadOnlyList<long> MembersAsOf(string date)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        return db.IndexMembership.ToList()
            .Where(m => string.CompareOrdinal(m.AddedOn, date) <= 0
                        && (m.RemovedOn is null || string.CompareOrdinal(date, m.RemovedOn) < 0))
            .Select(m => m.SecurityId)
            .Distinct()
            .ToList();
    }
}
