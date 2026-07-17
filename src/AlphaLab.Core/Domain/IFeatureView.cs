namespace AlphaLab.Core.Domain;

/// <summary>
/// The point-in-time data port (catalog §2, hard rule 4). A read-only accessor bounded by BOTH
/// <see cref="AsOf"/> and <see cref="Watermark"/>: it exposes only data timestamped ≤ AsOf, and
/// only row versions observed ≤ Watermark. Strategies never touch raw stores directly.
///
/// WHY THIS INTERFACE LIVES IN AlphaLab.Core, not AlphaLab.Data. Core has zero project
/// references (CI-enforced), so this surface is deliberately PRIMITIVES ONLY — SecurityId,
/// DateOnly, double. No BarRow, no EF type, nothing from Data appears in any signature. That is
/// what lets the port sit beside <see cref="IModel"/>, which needs it as a parameter. Were a
/// Data type to leak into this surface, IFeatureView would have to move to Data — and IModel
/// could not follow it there without breaking Strategies' contract. The implementation
/// (BarFeatureView, over IBarReadService) lives in Data, where Data → Core is legal.
///
/// LEAK-PROOF BY CONSTRUCTION: implementations resolve through the versioned bar reader, whose
/// watermark rule (latest version WHERE observed_at ≤ watermark) already exists and is tested.
/// This port adds no second place for point-in-time logic to drift.
///
/// Returns null rather than throwing where data is absent: catalog §2 tells models to omit a
/// security that lacks enough history, so absence is an ordinary, expected answer here. Absence
/// becoming an ORDER, by contrast, is what fails closed (hard rule 10) — that is the sizer's and
/// the broker's job, not this reader's.
/// </summary>
public interface IFeatureView
{
    /// <summary>The decision date. Nothing after this is visible.</summary>
    DateOnly AsOf { get; }

    /// <summary>The run's data watermark (UTC ISO-8601). No row version observed after this is
    /// visible — which is what makes a replay pinned to the past reproduce byte-identically.</summary>
    string Watermark { get; }

    /// <summary>Adjusted close on <paramref name="date"/>, or null. SIGNALS use adjusted
    /// (D30/§13.8) — never mix adjusted and raw within an account.</summary>
    double? AdjClose(SecurityId id, DateOnly date);

    /// <summary>The last <paramref name="sessions"/> adjusted closes ending at (and including)
    /// <see cref="AsOf"/>, oldest first. Shorter than requested (or empty) when history is thin —
    /// the caller decides whether that is disqualifying.</summary>
    IReadOnlyList<double> AdjCloseSeries(SecurityId id, int sessions);

    /// <summary>Raw (unadjusted) close on <paramref name="date"/>, or null. The LEDGER uses raw
    /// prices (D30): real share counts at the prices actually printed.</summary>
    double? RawClose(SecurityId id, DateOnly date);

    /// <summary>Raw open on <paramref name="date"/>, or null. The fill price for an order decided
    /// at the prior close (MASTER §6: decide at close T, fill at next open T+1).</summary>
    double? RawOpen(SecurityId id, DateOnly date);

    /// <summary>21-session average daily volume in SHARES, or null when the window is incomplete.
    /// Drives the D43 participation cap and the dimensionless √(Q/ADV) impact ratio — both of
    /// which need shares, not notional, to be well-formed.</summary>
    double? Adv21Shares(SecurityId id);

    /// <summary>21-session average daily volume in USD NOTIONAL, or null. Drives the D43 spread
    /// BUCKET only (mega ≥ $400M/day, large ≥ $100M/day, other) — the one place notional is the
    /// right unit.</summary>
    double? Adv21Notional(SecurityId id);

    /// <summary>Realized daily volatility (stdev of daily adjusted returns) over
    /// <paramref name="window"/> sessions ending at <see cref="AsOf"/>, or null when the window
    /// is incomplete. The σ in D43's k·σ·√(Q/ADV).</summary>
    double? RealizedVolDaily(SecurityId id, int window);
}
