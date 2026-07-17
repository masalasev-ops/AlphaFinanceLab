namespace AlphaLab.Strategies;

/// <summary>
/// Resolves the cap-weight Buy&amp;Hold benchmark's ETF proxy symbol from the membership source
/// (STRATEGY_CATALOG §5.1, finding I). CONFIG-DRIVEN, NOT HARDCODED: the proxy must follow the
/// universe the strategies actually trade, so it is keyed off <c>Universe.Bootstrap.MembershipPrimary</c>
/// — <c>oef_csv</c> ⇒ <c>OEF.US</c> (the S&amp;P 100 slice, D70) — and flips to <c>IVV.US</c> for
/// <c>ivv_csv</c> the moment the universe widens to the S&amp;P 500, with no code change.
///
/// Why not IVV always: the traded universe through Phase-4 sign-off is the S&amp;P 100 slice, and
/// benchmarking a strategy against a DIFFERENT universe (the S&amp;P 500) than it trades is the bias
/// finding I exists to close.
///
/// EXPENSE-RATIO BIAS (disclosed, not silent). The proxy is a real ETF, so its own fee drag
/// (~0.20%/yr for OEF, ~0.03%/yr for IVV) sits inside the benchmark. A strategy is therefore measured
/// against a slightly-handicapped baseline — a known, bounded, one-directional bias that flatters the
/// strategy by the fund's fee. It is documented here and in §5.1 rather than corrected: the honest
/// move is to name it, since the alternative (a synthetic cap-weight index) would trade one modelled
/// approximation for another. The EQUAL-weight benchmark is self-built precisely to avoid this (D68).
///
/// Fail closed (rule 10) on an unknown source rather than defaulting to a symbol the operator never chose.
/// </summary>
public static class CapWeightProxy
{
    public const string OefSource = "oef_csv";
    public const string IvvSource = "ivv_csv";

    public const string OefSymbol = "OEF.US"; // iShares S&P 100 ETF
    public const string IvvSymbol = "IVV.US"; // iShares Core S&P 500 ETF

    /// <summary>The EODHD symbol of the cap-weight proxy for a membership source. Throws on an
    /// unknown source (fail closed) — never a silent default.</summary>
    public static string SymbolFor(string membershipPrimary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(membershipPrimary);
        return membershipPrimary switch
        {
            OefSource => OefSymbol,
            IvvSource => IvvSymbol,
            _ => throw new NotSupportedException(
                $"No cap-weight ETF proxy is mapped for membership source '{membershipPrimary}'. " +
                $"Known: '{OefSource}' ⇒ {OefSymbol} (S&P 100 slice), '{IvvSource}' ⇒ {IvvSymbol} (S&P 500). " +
                "Refusing to guess a benchmark symbol (rule 10).")
        };
    }
}
