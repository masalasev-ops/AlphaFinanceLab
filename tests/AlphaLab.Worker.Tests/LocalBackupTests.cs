using System.Globalization;
using AlphaLab.Data.Entities;
using AlphaLab.Worker.Tests.Pipeline;
using Microsoft.Data.Sqlite;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The D72 per-launch local backup (checkpoint 2.12 / RUNBOOK §3): checkpoint the WAL, copy to a dated file
/// under the store's sibling backups\ dir (skip if today's exists), prune copies older than
/// Ops.BackupRetentionDays. Driven over the harness's real on-disk arena.
/// </summary>
public class LocalBackupTests
{
    [Fact]
    public async Task Backup_WritesADatedCopy()
    {
        using var h = new PipelineHarness();

        var result = await h.RunBackupAsync();

        Assert.True(result.Created);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        var expectedName = $"alphalab-{h.Now.UtcDateTime:yyyy-MM-dd}.db";
        Assert.Equal(expectedName, Path.GetFileName(result.BackupPath));
        Assert.Equal(h.BackupDir, Path.GetDirectoryName(result.BackupPath));
    }

    [Fact]
    public async Task Backup_SecondLaunchSameDay_SkipsTheCopy()
    {
        using var h = new PipelineHarness();

        var first = await h.RunBackupAsync();
        var second = await h.RunBackupAsync();

        Assert.True(first.Created);
        Assert.False(second.Created);              // today's copy already exists — not overwritten
        Assert.Equal(first.BackupPath, second.BackupPath);
    }

    [Fact]
    public async Task Backup_PrunesCopiesOlderThanRetention()
    {
        using var h = new PipelineHarness();
        h.OpsOptions.BackupRetentionDays = 30;

        // Pre-seed an ancient backup that is well outside the retention window.
        Directory.CreateDirectory(h.BackupDir);
        var stale = Path.Combine(h.BackupDir, "alphalab-2020-01-01.db");
        File.WriteAllText(stale, "old");

        var result = await h.RunBackupAsync();

        Assert.True(result.Created);
        Assert.Equal(1, result.Pruned);
        Assert.False(File.Exists(stale));               // the ancient copy was pruned
        Assert.True(File.Exists(result.BackupPath));    // today's copy remains
    }

    // ---- finding MM: a busy checkpoint fails CLOSED — no file of unknown integrity is ever written ----
    [Fact]
    public async Task Backup_CheckpointBusy_AbortsAndWritesNoFile()
    {
        using var h = new PipelineHarness();

        // Commit a write so the WAL holds frames a reader can pin.
        using (var db = h.Open())
        {
            db.TradingCalendar.Add(new TradingCalendarRow { Date = "2099-01-01", Session = "full", CloseTimeLocal = "16:00" });
            db.SaveChanges();
        }

        // Hold a second reader connection MID-READ (an open deferred read transaction): TRUNCATE cannot
        // reset the WAL while any reader is using it, so the checkpoint reports busy — the supported
        // separate-reader-process (Api) topology, reproduced in-process.
        using var reader = new SqliteConnection($"Data Source={h.DbPath}");
        reader.Open();
        using var txn = reader.BeginTransaction(deferred: true);
        using var cmd = reader.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = "SELECT COUNT(*) FROM trading_calendar;";
        cmd.ExecuteScalar(); // starts the read snapshot — the WAL read-mark is now held

        var result = await h.RunBackupAsync();

        Assert.True(result.Failed);
        Assert.False(result.Created);
        Assert.Null(result.BackupPath);
        Assert.Contains("busy", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        // NO backup file was written (the directory may exist; it must be empty of backups).
        var files = Directory.Exists(h.BackupDir)
            ? Directory.EnumerateFiles(h.BackupDir, "alphalab-*.db").ToList()
            : [];
        Assert.Empty(files);
    }

    [Theory]
    [InlineData("alphalab-2026-07-17.db", true, "2026-07-17")]
    [InlineData("alphalab-2020-01-01.db", true, "2020-01-01")]
    [InlineData("alphalab-notadate.db", false, null)]
    [InlineData("alphalab-2026-07-17.txt", false, null)]
    [InlineData("something-else.db", false, null)]
    public void TryParseBackupDate_ParsesOnlyWellFormedNames(string fileName, bool ok, string? iso)
    {
        var parsed = LocalBackup.TryParseBackupDate(fileName, out var date);

        Assert.Equal(ok, parsed);
        if (ok) Assert.Equal(DateOnly.ParseExact(iso!, "yyyy-MM-dd", CultureInfo.InvariantCulture), date);
    }
}
