using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>One security's GICS classification as read from the sector source. At launch the source is
/// the IVV/OEF holdings CSV's Sector column (Universe.SectorSource=ivv_csv), which carries no industry
/// — so <see cref="Industry"/> is null until the EODHD-fundamentals upgrade (D49). A null field leaves
/// the security's current value unchanged.</summary>
public sealed record SectorAssignment(long SecurityId, string? Sector, string? Industry = null);

/// <summary>
/// Applies GICS sector/industry classifications to securities and logs reclassifications (FR-5 / D35).
/// A change from a prior NON-NULL classification writes a <c>sector_changes</c> row (old/new) and
/// updates <c>securities</c>; an initial classification (null → X) sets the baseline WITHOUT a change
/// row; an unchanged classification is a no-op (idempotent). Never deletes. (The LowVol-at-next-rebalance
/// consumption of the change log is a later phase — 1.6 lands the log + the current-value update.)
/// </summary>
public interface ISectorIngestion
{
    /// <summary>Apply the classifications as of <paramref name="changedOn"/>. Returns the number of
    /// <c>sector_changes</c> rows written (reclassifications only, not baseline sets).</summary>
    int ApplySectors(IReadOnlyList<SectorAssignment> assignments, string changedOn);
}

public sealed class SectorIngestion(AlphaLabDbContext db) : ISectorIngestion
{
    public int ApplySectors(IReadOnlyList<SectorAssignment> assignments, string changedOn)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedOn);

        var changes = 0;
        foreach (var a in assignments)
        {
            var sec = db.Securities.Find(a.SecurityId);
            if (sec is null) continue;

            var newSector = a.Sector ?? sec.Sector;       // a null field leaves the current value
            var newIndustry = a.Industry ?? sec.Industry;

            var sectorChanged = !string.Equals(sec.Sector, newSector, StringComparison.Ordinal);
            var industryChanged = !string.Equals(sec.Industry, newIndustry, StringComparison.Ordinal);
            var priorClassified = sec.Sector is not null || sec.Industry is not null;

            // A reclassification (prior classification differs) is logged; an initial set is a baseline.
            if ((sectorChanged || industryChanged) && priorClassified)
            {
                db.SectorChanges.Add(new SectorChangeRow
                {
                    SecurityId = sec.SecurityId,
                    ChangedOn = changedOn,
                    OldSector = sec.Sector,
                    NewSector = newSector,
                    OldIndustry = sec.Industry,
                    NewIndustry = newIndustry
                });
                changes++;
            }

            sec.Sector = newSector;
            sec.Industry = newIndustry;
        }
        db.SaveChanges();
        return changes;
    }
}
