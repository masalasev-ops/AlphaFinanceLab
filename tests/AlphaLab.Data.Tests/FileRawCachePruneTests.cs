using AlphaLab.Data.Http;

namespace AlphaLab.Data.Tests;

/// <summary>
/// P1R-5: the documented 30-day raw-cache retention (INTEGRATIONS §9) is enforced by
/// <see cref="FileRawCache.Prune"/>. Age is by file mtime (robust to the un-dated `latest` partitions
/// and any mis-partitioning); deletion is confined to the cache root; now-empty partition dirs are swept
/// but the root itself never is.
/// </summary>
public class FileRawCachePruneTests
{
    [Fact]
    public void Prune_RemovesOld_KeepsFresh_SweepsEmptyDirs_RefusesOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "alphalab-prune-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "alphalab-prune-outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new FileRawCache(root);
            cache.Save("eodhd", "2026-07-15", "AAPL.eod.json", "[]"); // fresh (recent mtime)
            cache.Save("eodhd", "2006-07-15", "OLD.div.json", "[]");  // will be aged past the window

            var oldFile = Path.Combine(root, "eodhd", "2006-07-15", "OLD.div.json");
            var oldDir = Path.Combine(root, "eodhd", "2006-07-15");
            var freshFile = Path.Combine(root, "eodhd", "2026-07-15", "AAPL.eod.json");
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-40));

            // An old file OUTSIDE the root must never be touched.
            Directory.CreateDirectory(outside);
            var outsideFile = Path.Combine(outside, "keep.json");
            File.WriteAllText(outsideFile, "[]");
            File.SetLastWriteTimeUtc(outsideFile, DateTime.UtcNow.AddDays(-40));

            var deleted = cache.Prune(DateTime.UtcNow);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(oldFile));     // old file removed
            Assert.False(Directory.Exists(oldDir)); // its now-empty partition dir swept
            Assert.True(File.Exists(freshFile));    // fresh kept (its dir survives)
            Assert.True(Directory.Exists(root));    // the root itself is never removed
            Assert.True(File.Exists(outsideFile));  // outside the root: untouched
        }
        finally
        {
            foreach (var d in new[] { root, outside })
                try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Prune_WhitespaceRoot_FailsClosed()
    {
        // Never enumerate an unbounded/empty path — fail closed rather than risk a wide delete.
        Assert.Throws<ArgumentException>(() => new FileRawCache("  ").Prune(DateTime.UtcNow));
    }

    [Fact]
    public void Prune_MissingRoot_IsANoOp()
    {
        var root = Path.Combine(Path.GetTempPath(), "alphalab-prune-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, new FileRawCache(root).Prune(DateTime.UtcNow)); // nothing to prune, no throw
    }
}
