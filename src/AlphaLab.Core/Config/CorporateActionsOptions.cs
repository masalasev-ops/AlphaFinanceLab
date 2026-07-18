namespace AlphaLab.Core.Config;

/// <summary>
/// The §13.6 forced-event ledger's configurable rules (CONFIG "CorporateActions"). Two knobs the
/// design says are config but never had a key (CHANGELOG findings B, C): the delist bankruptcy
/// haircut and the spin-off liquidation rule.
///
/// Both govern feeds that are DORMANT at launch (D49 — no delist/spin-off actions are ingested), so
/// neither fires on live data in Phase 2; the semantics + fixtures ship now and production behaviour
/// is freeze+alert until the feeds turn on. The keys exist so the behaviour is falsifiable and
/// stated rather than hard-coded.
///
/// Follows the …Options convention (SectionName + mutable get/set defaults matching CONFIG).
/// </summary>
public sealed class CorporateActionsOptions
{
    public const string SectionName = "CorporateActions";

    /// <summary>
    /// Delist force-exit haircut, as a PERCENT (finding B; §13.6 "bankruptcy haircut configurable",
    /// D30). A terminal delist exits at <c>last_print × (1 − BankruptcyHaircutPct/100)</c>.
    ///
    /// Default 0 — take the last available print, inventing no loss the feed cannot substantiate.
    /// This is the least-assumptive choice, and the tension is real and deliberate: a "terminal, no
    /// successor" delist is usually distress, so 0% can flatter a genuine wipeout — but a large
    /// default would fabricate a loss on a name whose last print may already reflect the distress.
    /// The honest resolution is a KNOWN-severity override: the operator raises this (Phase-7 admin
    /// action / a versioned config row) when a specific delist is a confirmed bankruptcy. Since the
    /// delist feed is dormant (D49), the default never fires in Phase 2.
    /// </summary>
    public double BankruptcyHaircutPct { get; set; } = 0.0;

    /// <summary>
    /// Scheduled liquidation of a spun-off receipt (finding C; §13.6 "a scheduled liquidation rule,
    /// config"). 0 = EXIT-ONLY: the spun-off position is held and managed by the owning strategy's
    /// ExitPolicy (the default). &gt;0 = liquidate the receipt after N sessions.
    ///
    /// PHASE-2 NOTE: only the exit-only default (0) is EXECUTED here. The scheduled-liquidation
    /// EXECUTION (selling the receipt after N days) is a Stage-4 behaviour that lands with the risk
    /// system (Phase 7); the key is carried for CONFIG fidelity and PROGRESS records the line, so a
    /// non-zero value that does nothing yet is a documented deferral, not a silent no-op.
    /// </summary>
    public int SpinoffLiquidationDays { get; set; } = 0;
}
