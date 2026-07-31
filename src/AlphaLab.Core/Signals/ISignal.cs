using AlphaLab.Core.Domain;

namespace AlphaLab.Core.Signals;

/// <summary>
/// A pre-registered cross-sectional scoring rule (D91, MASTER §24; FR-43). It ranks the eligible
/// universe on a day and nothing else: no positions, no costs, no exits, no account. The Signal
/// Library grades it by rank-IC; a Phase-6 IModel may wrap the SAME instance for its scoring stage.
///
/// WHY THIS LIVES IN AlphaLab.Core AND NOT AlphaLab.Evaluation (finding 295). The CI reference graph
/// makes <c>AlphaLab.Strategies</c> and <c>AlphaLab.Evaluation</c> SIBLINGS — neither may reference
/// the other. §24.6 requires that "Phase 6 IModels wrap the same ISignal implementations", which is
/// satisfiable only if the contract and its scorers sit in the one project both can see. Core is that
/// project, and it is the honest home anyway: a scorer is pure arithmetic over <see cref="IFeatureView"/>,
/// which is already here for exactly the same reason. The consequence is the point — the library path
/// and the strategy path do not merely agree, they execute the identical type, so
/// <c>FX-SignalParity</c> (Phase 6) becomes a regression test over a shape that cannot diverge rather
/// than a discovery test over two implementations that might.
///
/// DESCRIPTIVE ONLY (§24.5, rule enforced in CI): nothing here may become an input to the allocator
/// (D51), any gate, sizing, or eligibility. The guard is structural — see the reference/consumer
/// closure test and the ci.ps1 scan — because a convention that only a comment defends is not a rule.
///
/// ABSENCE IS AN ANSWER, not an error: a name with too little history is OMITTED from the returned
/// map (catalog §2, the idiom every IModel already follows). The IC engine then grades the names it
/// actually scored, and <c>signal_ic.n</c> records how many that was (finding 294).
/// </summary>
public interface ISignal
{
    /// <summary>Registry id, e.g. <c>mom:L252s21</c> — the <c>signals.signal_id</c> primary key.</summary>
    string SignalId { get; }

    /// <summary>Catalog family: momentum | reversal | lowvol | breakout | resmom | bab.</summary>
    string Family { get; }

    /// <summary>
    /// The frozen parameters, serialized verbatim into <c>signals.config_json</c> at registration.
    /// FROZEN (D17/D91): a parameter change is a NEW registration (compiled code plus a doc change),
    /// never an edit — and there is no sweeping of params to maximize IC, because a sweep is candidate
    /// selection and candidate selection belongs to the arena, where it pays the trials tax (§24.3).
    /// </summary>
    IReadOnlyDictionary<string, double> Params { get; }

    /// <summary>
    /// The shipped implementation version the grades were computed by (<c>signals.code_version</c>).
    /// It exists so a grade record can be read against the exact arithmetic that produced it.
    /// </summary>
    string CodeVersion { get; }

    /// <summary>
    /// Score each eligible name, omitting any the rule cannot score. Pure and point-in-time: it reads
    /// only the watermark-bounded <paramref name="features"/> (rule 4 / F-DET), so recomputing a day
    /// at the same watermark is byte-identical (<c>FX-SignalIcDeterminism</c>).
    /// </summary>
    IReadOnlyDictionary<SecurityId, double> Score(
        IReadOnlyList<SecurityId> eligible, IFeatureView features, SignalContext context);
}

/// <summary>
/// The non-price inputs a scorer may need beyond <see cref="IFeatureView"/>. Today that is the market
/// proxy, which <c>resmom:L252</c> and <c>bab:L252</c> regress against — it is resolved per run from
/// the versioned <c>Regime.ProxySecurityId</c> config row, so it is a runtime value rather than a
/// frozen parameter and does not belong in <see cref="ISignal.Params"/>.
///
/// A scorer that needs the proxy and is handed none returns an EMPTY map rather than guessing: the
/// same absence-is-honest rule as thin history, and the fail-closed direction (rule 10).
/// </summary>
/// <param name="MarketProxy">The market proxy security, or null when none is resolved.</param>
public sealed record SignalContext(SecurityId? MarketProxy = null);
