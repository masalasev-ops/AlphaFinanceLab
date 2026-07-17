namespace AlphaLab.Core.Domain;

/// <summary>
/// The permanent internal identity of a security (D39, hard rule 2). Tickers are time-ranged
/// display aliases resolved through ticker_history; they are NEVER an identity.
///
/// This is a wrapper rather than a bare long on purpose: catalog §2 requires that a model's
/// score keys are security ids, and a bare long would let a ticker-derived int, an account_id,
/// or a run_id bind to the same parameter silently. Wrapping makes "keys are security_ids,
/// never raw tickers" a COMPILE-TIME fact instead of a code-review convention.
///
/// Deliberately NOT the same trade as money (see the ledger, where plain decimal is used): a
/// money wrapper would buy no correctness over decimal's own exactness, whereas this wrapper
/// buys type-separation between three interchangeable longs.
/// </summary>
public readonly record struct SecurityId(long Value)
{
    public override string ToString() => Value.ToString();

    /// <summary>Explicit, so a raw long can never bind implicitly (that would defeat the point).</summary>
    public static explicit operator long(SecurityId id) => id.Value;
}
