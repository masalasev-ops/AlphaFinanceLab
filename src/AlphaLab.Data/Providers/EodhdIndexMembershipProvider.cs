namespace AlphaLab.Data.Providers;

/// <summary>
/// DORMANT (D49): the EODHD index-constituents membership provider. The constituents endpoint
/// (/fundamentals/GSPC.INDX) is off the launch "All World" tier, so this stays behind
/// <c>Universe.MembershipPrimary</c> and is never wired active in 1.4. It is built now so the
/// post-upgrade flip to eodhd-primary is a config change, not new code. No live fixture is captured
/// at launch; <see cref="GetMembersAsync"/> fails loud rather than silently returning an empty set.
/// The parse skeleton (INTEGRATIONS §1 constituents shape) lands with the fundamentals upgrade.
/// </summary>
public sealed class EodhdIndexMembershipProvider : IIndexMembershipProvider
{
    public const string DormantReason =
        "EodhdIndexMembershipProvider is dormant (D49): the EODHD constituents endpoint is off the " +
        "launch tier. Activate via Universe.MembershipPrimary='eodhd' on the post-upgrade tier.";

    public Task<MembershipSnapshot> GetMembersAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(DormantReason);
}
