namespace AlphaLab.Core.Signals;

/// <summary>
/// The v1 pre-registered signal set (D91, MASTER §24.3; FR-43). Seven cross-sectional rules drawn from
/// the documented catalog families, frozen at registration.
///
/// PRE-REGISTERED means the set is fixed before any grade exists, and a change is a NEW registration
/// (compiled code plus a doc change), never an edit to a live row. There is deliberately NO sweep of
/// parameters to maximize IC: a sweep is candidate selection, and candidate selection belongs to the
/// arena where it pays the trials tax (rule 8 / D52 / §24.3).
///
/// TSMOM is excluded from v1 on principle, not oversight (§24.3): it is a time-series rule rather than
/// a cross-sectional ranking, so rank-IC is the wrong grade for it. It would register later with its
/// own grade definition (per-name directional hit rate).
///
/// A registration is NOT a candidate (D99): it writes no `strategies` row, no `trials_registry` row,
/// and never reaches `CandidateFactory`.
/// </summary>
public static class SignalRegistry
{
    /// <summary>
    /// The shipped scorer-implementation version stamped into <c>signals.code_version</c>. Bump it when
    /// the arithmetic of any scorer changes, so a grade record can always be read against the exact
    /// implementation that produced it.
    /// </summary>
    public const string CodeVersion = "v1";

    /// <summary>The seven v1 instruments, in registry order. One instance each — the same objects a
    /// Phase-6 IModel wraps (finding 295), which is what makes parity structural.</summary>
    public static IReadOnlyList<ISignal> V1 { get; } =
    [
        new MomentumSkipSignal(),
        new MediumMomentumSignal(),
        new ShortReversalSignal(),
        new LowVolSignal(),
        new BreakoutSignal(),
        new ResidualMomentumSignal(),
        new BettingAgainstBetaSignal(),
    ];

    /// <summary>Look up a registered signal by id, or null when it is not part of the v1 set.</summary>
    public static ISignal? ById(string signalId) =>
        V1.FirstOrDefault(s => string.Equals(s.SignalId, signalId, StringComparison.Ordinal));
}
