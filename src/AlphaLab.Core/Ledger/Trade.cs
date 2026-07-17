using System.Text.Json.Serialization;
using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Ledger;

/// <summary>Which side of the book (SCHEMA trades.side CHECK IN ('buy','sell')).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TradeSide>))]
public enum TradeSide
{
    Buy,
    Sell,
}

/// <summary>
/// Why a trade happened (SCHEMA trades.reason).
///
/// This enum is the audit trail for hard rule 7: the wish list opens and adds
/// (<see cref="Wishlist"/>); ONLY the ExitPolicy closes (<see cref="ExitPolicy"/>) — plus forced
/// events (<see cref="CorpAction"/>, <see cref="Guardrail"/>). There is deliberately NO
/// "fell off the wish list" reason, because that is never a sell.
///
/// SCHEMA declares no CHECK on trades.reason; the constraint lives here and in the funnel.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TradeReason>))]
public enum TradeReason
{
    /// <summary>Opened or added because the name is on today's wish list.</summary>
    Wishlist,

    /// <summary>Closed because the strategy's ExitPolicy said so. The ONLY signal-driven close.</summary>
    ExitPolicy,

    /// <summary>Forced by a corporate action (§13.6): merger cash-out/conversion, spin-off
    /// receipt, delist force-exit. Carries the action_id that caused it.</summary>
    CorpAction,

    /// <summary>Forced by a guardrail circuit-breaker.</summary>
    Guardrail,
}

/// <summary>
/// A filled trade (SCHEMA trades). Every money field is decimal → TEXT (D69).
///
/// DECIDE AT CLOSE T, FILL AT NEXT OPEN T+1 (MASTER §6): <see cref="DecidedOn"/> and
/// <see cref="FilledOn"/> are different days on purpose — a strategy never trades on a bar it
/// could not have acted on. <see cref="RawFillPrice"/> is the raw next open (D30).
///
/// <see cref="CostModelVersion"/> stamps every fill (D43). Whether a strategy survives net of
/// costs is the most consequential number in the system, so every fill stays attributable to the
/// exact cost model that priced it.
/// </summary>
public sealed record Trade
{
    /// <summary>trades.trade_id. Zero until the store assigns the rowid.</summary>
    public long TradeId { get; init; }

    public required long AccountId { get; init; }

    public required SecurityId SecurityId { get; init; }

    public required TradeSide Side { get; init; }

    /// <summary>ISO date of the close at which the decision was made (T).</summary>
    public required string DecidedOn { get; init; }

    /// <summary>ISO date of the open at which it filled (T+1).</summary>
    public required string FilledOn { get; init; }

    public required double Shares { get; init; }

    /// <summary>The raw (unadjusted) fill price — the next open (D30).</summary>
    public required decimal RawFillPrice { get; init; }

    public required decimal Commission { get; init; }
    public required decimal SpreadCost { get; init; }
    public required decimal ImpactCost { get; init; }

    /// <summary>D43 stamp, e.g. "cm-1.0".</summary>
    public required string CostModelVersion { get; init; }

    public required TradeReason Reason { get; init; }

    /// <summary>The corporate action that forced this trade; non-null iff
    /// <see cref="Reason"/> == <see cref="TradeReason.CorpAction"/>.</summary>
    public long? ActionId { get; init; }

    public RunKind RunKind { get; init; } = RunKind.Live;

    /// <summary>Total transaction cost. Note a cash merger waives commission/spread/impact
    /// (§13.6: a corporate action is not a trade), so this is legitimately zero there.</summary>
    public decimal TotalCost => Commission + SpreadCost + ImpactCost;

    /// <summary>Signed cash effect of the fill including costs: a buy consumes cash, a sell
    /// releases it, and costs always reduce cash on both sides.</summary>
    public decimal CashDelta => Side == TradeSide.Buy
        ? -(RawFillPrice * (decimal)Shares) - TotalCost
        : (RawFillPrice * (decimal)Shares) - TotalCost;
}
