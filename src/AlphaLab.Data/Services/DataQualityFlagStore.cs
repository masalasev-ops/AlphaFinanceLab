using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>
/// Persists FR-6 gate findings into <c>data_quality_flags</c> (D77) and reads them back per run. The
/// gate (<see cref="DataQualityGate"/>) is PURE and produces symbol-keyed <see cref="QualityFlag"/>s;
/// this store is the sink that makes an alarm visible — "an alarm nobody can see is not an alarm." The
/// Data-health read-model reads these rows (Phase 7); wiring the gate→store into the D53 staged pipeline
/// is Phase 2. This seam lands the table so there is something to persist into.
/// </summary>
public interface IDataQualityFlagStore
{
    /// <summary>Append the gate's flags for one security under a run. <paramref name="securityId"/> is
    /// optional — the gate emits a symbol; a caller that has resolved an id may pass it. Persists BOTH
    /// warn and reject flags (the audit trail). Returns the number of rows written.</summary>
    int Save(long runId, long? securityId, IReadOnlyList<QualityFlag> flags, string observedAt);

    /// <summary>All flags recorded under a run, in insertion order.</summary>
    IReadOnlyList<DataQualityFlagRow> GetForRun(long runId);
}

public sealed class DataQualityFlagStore(AlphaLabDbContext db) : IDataQualityFlagStore
{
    public int Save(long runId, long? securityId, IReadOnlyList<QualityFlag> flags, string observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedAt);
        foreach (var f in flags)
        {
            db.DataQualityFlags.Add(new DataQualityFlagRow
            {
                RunId = runId,
                SecurityId = securityId,
                Symbol = f.Symbol,
                Date = f.Date,
                Issue = IssueToken(f.Issue),
                Severity = SeverityToken(f.Severity),
                Detail = f.Detail,
                ObservedAt = observedAt
            });
        }
        db.SaveChanges();
        return flags.Count;
    }

    public IReadOnlyList<DataQualityFlagRow> GetForRun(long runId) =>
        db.DataQualityFlags.Where(x => x.RunId == runId).OrderBy(x => x.FlagId).ToList();

    // enum → the lowercase snake_case DB tokens the CHECK constraints enforce (fail closed on an
    // unmapped value rather than writing a token the CHECK would reject at SaveChanges).
    private static string IssueToken(QualityIssue issue) => issue switch
    {
        QualityIssue.MissingBar => "missing_bar",
        QualityIssue.NanField => "nan_field",
        QualityIssue.NonPositivePrice => "non_positive_price",
        QualityIssue.OutlierReturn => "outlier_return",
        QualityIssue.UnexplainedAdjustment => "unexplained_adjustment",
        QualityIssue.CrossCheckMismatch => "cross_check_mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(issue), issue, "unmapped QualityIssue")
    };

    private static string SeverityToken(QualitySeverity severity) => severity switch
    {
        QualitySeverity.Warn => "warn",
        QualitySeverity.Reject => "reject",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "unmapped QualitySeverity")
    };
}
