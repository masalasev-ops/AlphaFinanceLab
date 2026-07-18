using System.Text.Json.Serialization;

namespace AlphaLab.Core.Ledger;

/// <summary>
/// Which track a ledger row belongs to (SCHEMA run_kind on accounts/trades/cash_events/
/// equity_curve/decisions).
///
/// This is the QUARANTINE discriminant (hard rule 1 / D37). Forward paper P&amp;L judges
/// strategies; replay judges only the machinery and is quarantined from every forward view, gate
/// input, and chart. `live` and `catchup` are both FORWARD — catch-up is the same work on a day
/// the machine was off, not a different kind of evidence. Only `replay` is quarantined.
///
/// Note the deliberate asymmetry with runs.run_kind, which has three values ('live','catchup',
/// 'replay'): a ledger row is written by a forward run or a replay run, and the forward pair
/// collapses to one token here because nothing downstream may treat a caught-up day as lesser
/// evidence than a same-day one.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunKind>))]
public enum RunKind
{
    /// <summary>Forward paper trading — the evidence that judges strategies.</summary>
    Live,

    /// <summary>Replay (D37). Quarantined from every forward view by construction.</summary>
    Replay,
}

/// <summary>
/// A strategy's isolated paper-trading account (SCHEMA accounts). One strategy, one book — so a
/// strategy's P&amp;L is never contaminated by another's.
///
/// <see cref="StartingCash"/> and every other money value here is C# decimal persisted as TEXT
/// (D69) — never double/REAL.
/// </summary>
public sealed record Account
{
    /// <summary>accounts.account_id. Zero until the store assigns the rowid.</summary>
    public long AccountId { get; init; }

    public required string StrategyId { get; init; }

    public required decimal StartingCash { get; init; }

    public RunKind RunKind { get; init; } = RunKind.Live;
}
