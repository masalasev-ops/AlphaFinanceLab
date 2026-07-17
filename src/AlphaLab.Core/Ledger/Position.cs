using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Ledger;

/// <summary>
/// An open position (SCHEMA positions; PK (account_id, security_id)).
///
/// <see cref="CostBasis"/> is a RAW-price basis (D30): the ledger trades real share counts at
/// the prices actually printed, never adjusted prices. Signals use adjusted; the two are never
/// mixed within an account (§13.8). This is worth ~1.5%/yr of phantom alpha if got wrong.
///
/// <see cref="Shares"/> is double (REAL per SCHEMA), not decimal — a share count is a quantity,
/// not money, and splits produce genuine fractions. Money is decimal (D69).
/// </summary>
public sealed record Position
{
    public required long AccountId { get; init; }

    public required SecurityId SecurityId { get; init; }

    public required double Shares { get; init; }

    /// <summary>Total raw-price cost basis for the whole holding (decimal → TEXT, D69).</summary>
    public required decimal CostBasis { get; init; }

    /// <summary>ISO date the position was first opened.</summary>
    public required string OpenedOn { get; init; }

    /// <summary>
    /// The fail-closed flag (D39 / hard rule 10). Set when an unmapped corporate action or a bar
    /// stoppage without an event makes the position unpriceable: valuation freezes at the last
    /// print, the position is flagged on the Risk screen, and the operator is alerted. Never
    /// silently mispriced.
    /// </summary>
    public bool Frozen { get; init; }

    /// <summary>Why it froze. Non-null iff <see cref="Frozen"/> — an unexplained freeze is not an
    /// alert, it is a mystery.</summary>
    public string? FrozenReason { get; init; }
}
