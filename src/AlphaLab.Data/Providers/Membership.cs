namespace AlphaLab.Data.Providers;

/// <summary>One index member as surfaced by a membership provider. <see cref="CanonicalSymbol"/> is
/// the EODHD form (via <see cref="SymbolNormalizer"/>); <see cref="RawSymbol"/> keeps the source
/// spelling for audit; <see cref="Sector"/> is the source GICS sector where the feed carries it
/// (IVV/OEF do; the Wikipedia cross-check leaves it null in 1.4).</summary>
public sealed record MemberRow(string CanonicalSymbol, string RawSymbol, string? Sector);

/// <summary>A membership roster from one source. <see cref="Source"/> is the config source label
/// (e.g. "ivv_csv", "wikipedia").</summary>
public sealed record MembershipSnapshot(string Source, IReadOnlyList<MemberRow> Members);

/// <summary>
/// Index-membership source seam (FR-4 / D35/D49/D70). At launch the primary is the iShares holdings
/// CSV (IVV for the S&amp;P 500, OEF for the S&amp;P 100 slice) and the cross-check is Wikipedia; the
/// EODHD constituents provider is built but dormant (D49). Fetch hits the network; the static
/// <c>ToSnapshot</c> helpers on each concrete are pure and unit-tested offline against byte-real
/// fixtures.
/// </summary>
public interface IIndexMembershipProvider
{
    Task<MembershipSnapshot> GetMembersAsync(CancellationToken ct = default);
}
