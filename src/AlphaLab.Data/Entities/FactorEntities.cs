namespace AlphaLab.Data.Entities;

/// <summary>
/// One factor observation: (date, factor) → value. The Ken French Data Library daily series (D41),
/// ingested monthly. Shape is SCHEMA_v1.9 §factor_returns VERBATIM — three columns, composite PK, no
/// CHECK on <see cref="Factor"/> despite the token list in SCHEMA's comment.
///
/// **VALUES ARE DAILY DECIMAL RETURNS, NOT PERCENT.** The published CSV is in PERCENT (a 0.03% day is
/// written `0.03`), and the ingest divides by 100 before storing so that a factor column and a strategy
/// return column can be regressed against each other without a unit conversion at the call site. The
/// conversion belongs at the boundary, once, rather than at every reader — a reader that forgets it
/// would be wrong by 100× with no symptom other than an implausible β.
///
/// **NO `observed_at`, AND THAT IS SCHEMA'S SHAPE RATHER THAN AN OVERSIGHT HERE — see finding 443.**
/// The table therefore cannot answer "was this row publishable as of date X", which is what D83 requires
/// once residual momentum reads the series as a SIGNAL and what OVERFITTING_MONITOR §4 requires for S5's
/// PSI baseline and S8. That is a decision, not an implementation detail (phase6/README: "never author a
/// table SCHEMA already specifies"), so the column is not invented here; the gap is filed with its
/// trigger — checkpoint 6.13, the first use that is not diagnostic-only.
/// </summary>
public sealed class FactorReturnRow
{
    /// <summary>Trading date, `yyyy-MM-dd`.</summary>
    public string Date { get; set; } = default!;

    /// <summary>One of `MKT_RF`, `SMB`, `HML`, `UMD`, `RMW`, `CMA`, `RF` (SCHEMA's comment; deliberately
    /// NOT a CHECK constraint, because SCHEMA declares none and the shape is copied verbatim).</summary>
    public string Factor { get; set; } = default!;

    /// <summary>The daily return as a DECIMAL fraction (0.0003 = 3 bps), converted from the published
    /// percent at ingest.</summary>
    public double Value { get; set; }
}

/// <summary>
/// One row per refresh attempt that WROTE (D41). Shape is SCHEMA_v1.9 §factor_refresh_log verbatim.
///
/// **WHAT <see cref="Checksum"/> IS COMPARED AGAINST, stated here because a hash with no comparison
/// subject is a check that cannot fail.** The Ken French library publishes no digest, so this is not a
/// verification against an upstream value — it is a **stored fingerprint of the raw zip bytes**, and the
/// reachable failure is a CHANGED fingerprint for an overlapping window, which means upstream revised
/// history it had already published. That is a real alarm and it is the arm the refusal fixture fires.
/// On a FIRST fetch there is nothing to compare, so continuity — not the fingerprint — is what guards
/// that case. Hashing the RAW BYTES rather than decoded text is deliberate: the payload is a zip, and
/// the only in-repo precedent (`HistoricalBackfill.cs`) hashes a UTF-8 string, which this cannot reuse.
/// </summary>
public sealed class FactorRefreshLogRow
{
    /// <summary>UTC ISO-8601 instant the refresh ran — the PK, so one row per attempt that wrote.</summary>
    public string RefreshedAt { get; set; } = default!;

    /// <summary>The source files this refresh covered, as JSON — plural because D41's series arrives in
    /// TWO zips (5-factor + RF, and momentum).</summary>
    public string? FilesJson { get; set; }

    /// <summary>SHA-256 over the raw zip bytes, hex. See the type remarks for its comparison subject.</summary>
    public string? Checksum { get; set; }

    /// <summary>Rows inserted by this refresh.</summary>
    public int? RowsAdded { get; set; }
}
