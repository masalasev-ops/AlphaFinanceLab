using System.Text.Json.Serialization;
using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Ledger;

/// <summary>
/// Non-trade cash movements (SCHEMA cash_events.type). SCHEMA's comment reads
/// "dividend|merger_cash|deposit|..." and declares NO CHECK constraint on the column — the open
/// list is deliberate, so this enum stays the code-side contract rather than a DB constraint.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CashEventType>))]
public enum CashEventType
{
    /// <summary>Cash credited on the ex-date (D30). Carries the action_id.</summary>
    Dividend,

    /// <summary>The cash leg of a cash or mixed merger (§13.6).</summary>
    MergerCash,

    /// <summary>Opening capital (accounts.starting_cash's counterpart on the curve).</summary>
    Deposit,
}

/// <summary>
/// A cash movement that is not a fill (SCHEMA cash_events). <see cref="Amount"/> is decimal →
/// TEXT (D69); positive credits the account.
/// </summary>
public sealed record CashEvent
{
    /// <summary>cash_events.event_id. Zero until the store assigns the rowid.</summary>
    public long EventId { get; init; }

    public required long AccountId { get; init; }

    /// <summary>The security the cash came from; null for account-level events (a deposit).</summary>
    public SecurityId? SecurityId { get; init; }

    /// <summary>ISO date the cash lands. For a dividend this is the EX-DATE (D30) — not the
    /// payment date. The lab credits on ex-date because that is when the position's value drops.</summary>
    public required string AsOf { get; init; }

    public required CashEventType Type { get; init; }

    public required decimal Amount { get; init; }

    /// <summary>The corporate action that produced this cash; null for a deposit.</summary>
    public long? ActionId { get; init; }

    public RunKind RunKind { get; init; } = RunKind.Live;
}
