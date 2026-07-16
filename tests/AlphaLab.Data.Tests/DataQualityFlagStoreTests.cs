using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Data.Tests;

/// <summary>
/// D77 (FR-6): the DataQualityGate produces symbol-keyed QualityFlags; DataQualityFlagStore persists
/// them into data_quality_flags and reads them back per run — the sink that makes an alarm visible.
/// Persists BOTH warn and reject flags; the issue/severity enums map to the lowercase CHECK tokens, and
/// a bogus token is rejected by the DB CHECK (fail closed).
/// </summary>
public class DataQualityFlagStoreTests
{
    private const long RunId = 42;

    [Fact]
    public void D77_Save_ThenGetForRun_RoundTrips_WarnAndReject_WithTokenMapping()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            var flags = new List<QualityFlag>
            {
                new(QualityIssue.MissingBar, QualitySeverity.Warn, "AAPL", null, "Expected trading session has no bar (gap)."),
                new(QualityIssue.OutlierReturn, QualitySeverity.Warn, "AAPL", "2024-05-10", "robust z = 9.1"),
                new(QualityIssue.NanField, QualitySeverity.Reject, "AAPL", "2024-05-11", "NaN close"),
            };

            using (var db = TestDb.Open(path))
            {
                var written = new DataQualityFlagStore(db).Save(RunId, securityId: 7, flags, "2024-06-01T00:00:00Z");
                Assert.Equal(3, written);
            }

            using (var db = TestDb.Open(path))
            {
                var rows = new DataQualityFlagStore(db).GetForRun(RunId);
                Assert.Equal(3, rows.Count);

                // enum → lowercase CHECK tokens, in insertion order.
                Assert.Equal(["missing_bar", "outlier_return", "nan_field"], rows.Select(r => r.Issue));
                Assert.Equal(["warn", "warn", "reject"], rows.Select(r => r.Severity));

                Assert.All(rows, r => Assert.Equal("AAPL", r.Symbol));
                Assert.All(rows, r => Assert.Equal(7, r.SecurityId));
                Assert.All(rows, r => Assert.Equal("2024-06-01T00:00:00Z", r.ObservedAt));
                Assert.Null(rows[0].Date);              // a series/gap flag has no single date
                Assert.Equal("2024-05-10", rows[1].Date);
                Assert.Equal("robust z = 9.1", rows[1].Detail);
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void D77_GetForRun_IsScopedToTheRun()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using (var db = TestDb.Open(path))
            {
                var store = new DataQualityFlagStore(db);
                store.Save(RunId, null, [new(QualityIssue.MissingBar, QualitySeverity.Warn, "AAPL", null, "gap")], "t");
                store.Save(RunId + 1, null, [new(QualityIssue.NanField, QualitySeverity.Reject, "MSFT", "2024-01-02", "NaN")], "t");
            }
            using (var db = TestDb.Open(path))
            {
                Assert.Single(new DataQualityFlagStore(db).GetForRun(RunId));
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Theory]
    [InlineData("not_an_issue", "warn")]   // bogus issue
    [InlineData("outlier_return", "maybe")] // bogus severity
    public void D77_Check_RejectsUnknownIssueOrSeverity(string issue, string severity)
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            db.DataQualityFlags.Add(new DataQualityFlagRow
            {
                RunId = RunId, Symbol = "AAPL", Issue = issue, Severity = severity,
                Detail = "x", ObservedAt = "2024-06-01T00:00:00Z"
            });
            Assert.ThrowsAny<Exception>(() => db.SaveChanges());
        }
        finally { TestDb.Delete(path); }
    }
}
