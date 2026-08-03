using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>One security's gate findings — the unit of the batch save (finding 358).</summary>
public readonly record struct SecurityFlags(long? SecurityId, IReadOnlyList<QualityFlag> Flags);

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

    /// <summary>
    /// Append MANY securities' flags under one run in a SINGLE round-trip (finding 358). The per-security
    /// overload saves once per call, so a pipeline day that flags hundreds of securities issued hundreds
    /// of SaveChanges, and EVERY one of them re-ran DetectChanges over everything already tracked that
    /// day — the same defect finding 354 removed from equity points and finding 357 from the ledger
    /// reads, a third time. Rows are appended in the order given, so <see cref="GetForRun"/>'s
    /// insertion-order contract is unchanged. Returns the total number of rows written.
    /// </summary>
    int Save(long runId, IReadOnlyList<SecurityFlags> batch, string observedAt);

    /// <summary>All flags recorded under a run, in insertion order.</summary>
    IReadOnlyList<DataQualityFlagRow> GetForRun(long runId);
}

public sealed class DataQualityFlagStore(AlphaLabDbContext db) : IDataQualityFlagStore
{
    // The single-security call is the batch of one: ONE definition of the flag→row mapping, so the two
    // entry points can never drift into writing different rows for the same flag.
    public int Save(long runId, long? securityId, IReadOnlyList<QualityFlag> flags, string observedAt) =>
        Save(runId, [new SecurityFlags(securityId, flags)], observedAt);

    public int Save(long runId, IReadOnlyList<SecurityFlags> batch, string observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedAt);
        ArgumentNullException.ThrowIfNull(batch);

        var written = 0;
        foreach (var (securityId, flags) in batch)
        {
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
                written++;
            }
        }

        // ONE SaveChanges for the whole day (finding 358) — and only when there is something to write, so
        // an all-clean day does not pay a DetectChanges pass for zero rows (the finding 354 precedent).
        if (written > 0) db.SaveChanges();
        return written;
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
