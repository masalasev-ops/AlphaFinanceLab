using AlphaLab.Data;
using AlphaLab.Worker.Ops;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The schema-drift guard for the reproduce rewind (checkpoint 3.5.1).
///
/// <see cref="ScratchStore"/> decides, per table, whether a reproduction must rewind it, carry it
/// across, or handle it specially. A future migration that adds a table and forgets this decision is
/// the dangerous case: if the new table holds run OUTPUT and is silently carried across, the target
/// day's own rows survive the rewind and every reproduce-day run reports a vacuous match. Nothing
/// about that failure is visible at runtime — which is why it is caught here, at build time, by
/// forcing the classification to stay total over the model.
/// </summary>
public class ScratchStoreClassificationTests
{
    [Fact]
    public void FR25_ScratchStore_ClassifiesEveryTableInTheModel()
    {
        using var db = new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite("Data Source=:memory:").Options);

        var modelTables = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var classified = ScratchStore.ClassifiedTables.ToHashSet(StringComparer.Ordinal);
        var unclassified = modelTables.Where(t => !classified.Contains(t)).ToList();

        Assert.True(unclassified.Count == 0,
            "ScratchStore does not classify: " + string.Join(", ", unclassified) +
            ". Every table must be declared as rewound (a daily run writes it), untouched (with a stated " +
            "reason), or specially handled — otherwise a reproduce-day run can compare a day against its " +
            "own surviving output and pass vacuously.");
    }

    [Fact]
    public void FR25_ScratchStore_ClassificationHasNoStaleOrDuplicateEntries()
    {
        using var db = new AlphaLabDbContext(
            new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite("Data Source=:memory:").Options);

        var modelTables = db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => t is not null)
            .Select(t => t!)
            .ToHashSet(StringComparer.Ordinal);

        var classified = ScratchStore.ClassifiedTables;

        // A name in two buckets is ambiguous; a name in no model is a leftover from a dropped table.
        var duplicates = classified.GroupBy(t => t, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Classified in more than one bucket: " + string.Join(", ", duplicates));

        var stale = classified.Where(t => !modelTables.Contains(t)).ToList();
        Assert.True(stale.Count == 0, "Classified but not in the model: " + string.Join(", ", stale));
    }
}
