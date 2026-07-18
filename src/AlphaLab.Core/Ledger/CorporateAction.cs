using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Ledger;

/// <summary>
/// The eight corporate-action kinds (§13.6 / SCHEMA corporate_actions.type CHECK). This Core enum
/// mirrors the DB's string tokens; the Data adapter maps the two and fails closed on an unknown
/// token (rule 10) rather than defaulting to a benign kind.
/// </summary>
public enum CorporateActionType
{
    /// <summary>Cash credit on the ex-date (2.6).</summary>
    Dividend,

    /// <summary>Share count × ratio, per-share basis ÷ ratio, equity unchanged (2.6).</summary>
    Split,

    /// <summary>Alias update only — a NON-EVENT for the ledger (D39); identity is security_id (2.6).</summary>
    TickerChange,

    /// <summary>Position closed at deal cash per share, standard costs waived (2.7).</summary>
    MergerCash,

    /// <summary>Shares converted at the exchange ratio into the acquirer's security_id (2.7).</summary>
    MergerStock,

    /// <summary>Cash leg credited + stock leg converted (2.7).</summary>
    MergerMixed,

    /// <summary>New position created in the spun-off security_id, basis allocated by ratio (2.7).</summary>
    Spinoff,

    /// <summary>Force-exit at last print, bankruptcy haircut configurable (2.7).</summary>
    Delist,
}

/// <summary>
/// A corporate action as the LEDGER sees it (§13.6) — the Core mirror of
/// <c>CorporateActionRow</c>, carrying only what the ledger prices on. The persistence fields
/// (version, observed_at, source, processed_on) are Data's concern and never reach here; the Data
/// adapter has already resolved the watermark read (D76) before building this.
///
/// <see cref="ActionId"/> travels with it because every forced event is logged to cash_events /
/// trades WITH the action id that caused it (§13.6) — a forced fill whose cause is not recorded is
/// unauditable.
/// </summary>
public sealed record CorporateAction
{
    public required long ActionId { get; init; }

    public required SecurityId SecurityId { get; init; }

    public required CorporateActionType Type { get; init; }

    /// <summary>Ex-date (dividends). The dividend credits on THIS date, not the pay date (D30) —
    /// it is when the position's value drops.</summary>
    public string? ExDate { get; init; }

    /// <summary>The date the action takes effect (splits, ticker changes, mergers, …).</summary>
    public required string EffectiveDate { get; init; }

    /// <summary>Dividend / merger cash leg per share (decimal, D69).</summary>
    public decimal? CashPerShare { get; init; }

    /// <summary>Split / exchange / spin-off ratio (REAL).</summary>
    public double? Ratio { get; init; }

    /// <summary>The acquirer (stock/mixed merger) or parent (spin-off) — 2.7.</summary>
    public SecurityId? CounterpartySecurityId { get; init; }

    /// <summary>The new ticker (ticker change). Display only — never an identity (D39).</summary>
    public string? NewSymbol { get; init; }

    /// <summary>The date this action is applied for — the ex-date for a dividend, else the
    /// effective date. This is the date the ledger keys the day's application on.</summary>
    public string AppliedOn => Type == CorporateActionType.Dividend ? (ExDate ?? EffectiveDate) : EffectiveDate;
}
