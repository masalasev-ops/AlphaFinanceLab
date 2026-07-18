namespace AlphaLab.Core.Domain;

/// <summary>
/// A strategy (catalog §2). Every strategy runs the SAME six-stage funnel; they differ only at
/// Stage 2 (scoring, below) and at the ExitPolicy consulted in Stage 4. That is what makes any
/// difference in results genuinely about the strategy rather than about its plumbing.
/// </summary>
public interface IModel
{
    /// <summary>Stable identity, e.g. "momentum:L126:K21:N40". Persisted as strategies.strategy_id.</summary>
    string Id { get; }

    /// <summary>Frozen params + seed (D17), persisted as strategies.config_json.</summary>
    StrategyConfig Config { get; }

    /// <summary>How long the strategy intends to hold. The calibration target P(up over horizon)
    /// and any Kelly payoff estimate are defined over this.</summary>
    HoldingHorizon Horizon { get; }

    /// <summary>Declarative exit rules, executed by shared Stage-4 code. Stage 4 consults THIS
    /// for closes — never the wish list (hard rule 7).</summary>
    ExitPolicy Exits { get; }

    /// <summary>
    /// Score the eligible universe for one decision date.
    ///
    /// CONTRACT (catalog §2):
    ///  - MUST use only data timestamped ≤ asOf, resolved at the run's watermark — both are
    ///    already bounded by <paramref name="features"/>, which is the only data source a model
    ///    may touch (point-in-time; no look-ahead; D40/hard rule 4).
    ///  - Keys are SECURITY IDS (D39/hard rule 2), never raw tickers.
    ///  - Returns a signal in [0,1] per security: higher = more bullish. It is a SIGNAL, not yet
    ///    a probability — sizing that needs a probability gets it from a calibration map (§9).
    ///  - OMIT a security (or return null) if it lacks enough history/data. Omission is the
    ///    honest answer; a fabricated score is not.
    ///  - MUST be deterministic given (inputs, watermark, Config.Seed) — NFR-1 / F-DET.
    ///
    /// The whole universe is scored at once because cross-sectional strategies rank names
    /// AGAINST EACH OTHER, which needs the full set.
    ///
    /// Note Stage 3's invariant, which this method cannot opt out of: a name scoring 0 (or below
    /// MinScore) is never selectable. Returning 0 is how a model says "not this one".
    /// </summary>
    Task<IReadOnlyDictionary<SecurityId, double>> ScoreUniverseAsync(
        IReadOnlyList<SecurityId> eligible,
        DateOnly asOf,
        IFeatureView features,
        CancellationToken cancellationToken = default);
}
