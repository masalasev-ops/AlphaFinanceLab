using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data;
using AlphaLab.Data.Services;

namespace AlphaLab.Strategies;

/// <summary>
/// Opens the account an ADMITTED strategy needs in order to trade (Phase 6 checkpoint 6.2) — the second
/// half of the lifecycle seam, and the half that is easy to miss.
///
/// **`CandidateFactory` writes a `strategies` row and stops.** Nothing then opened an account, and the
/// daily pipeline iterates ACCOUNTS, not strategies — so a candidate could pass the D89 detectability
/// gate, spend a trial from a budget that raises the Bonferroni floor for every candidate after it, and
/// never trade a single day. It was not even reached to be warned about: with no account it is invisible
/// to the loop. Closing only the registry half would have fixed the *second* symptom and left this one.
///
/// **FORWARD ONLY, deliberately.** A replay opens its own accounts (D37), and generation 2 — the frozen
/// calibration the D106/D117 harness reproduces — was built from exactly the roster the replay seeds.
/// Opening candidate accounts inside a replay would change what trades there and put `FX-RecomputeParity`
/// at risk for no gain, since a candidate's forward track is the only thing that judges it (rule 1).
///
/// **Idempotent**, on `DummyRoster`'s shape: an account already open is reused, never duplicated, and
/// the account's opening deposit is written by <see cref="ILedgerStore.OpenAccount"/> so starting cash is
/// never a number only the accounts row knows about.
/// </summary>
public sealed class StrategyRoster(AlphaLabDbContext db, ILedgerStore ledger)
{
    /// <summary>
    /// Statuses that trade a forward account. `baseline` is excluded because `DummyRoster` owns the two
    /// benchmarks; `retired` is excluded because a retirement is an ending. `control` IS included: the
    /// D81 no-LLM twin is a control that trades its own account and is simply never promotable.
    /// </summary>
    private static readonly string[] Tradable = ["candidate", "live", "control"];

    /// <summary>
    /// Open an account for every admitted strategy that can be run and does not have one yet. Returns
    /// the strategy ids newly opened, so the caller can log what entered the arena today.
    /// </summary>
    public IReadOnlyList<string> OpenMissingAccounts(string asOf, RunKind runKind = RunKind.Live)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        if (runKind != RunKind.Live) return [];   // forward only — see the class comment

        var startingCash = new DummyRoster(db, ledger)
            .ResolveStartingCash(asOf, DummyRoster.DefaultStartingCash);

        var haveAccounts = ledger.GetAccounts(runKind)
            .Select(a => a.StrategyId)
            .ToHashSet(StringComparer.Ordinal);

        var admitted = db.Strategies
            .Where(s => Tradable.Contains(s.Status))
            .OrderBy(s => s.StrategyId)          // deterministic open order (F-DET)
            .ToList();

        var opened = new List<string>();
        foreach (var row in admitted)
        {
            if (haveAccounts.Contains(row.StrategyId)) continue;

            // Rule 10: an admitted row this build cannot construct does NOT get an account. Opening one
            // would create an account the funnel then skips every day — a strategy that looks live in the
            // ledger and never trades, which is a worse lie than the missing account it replaced.
            if (StrategyRegistry.ForRow(row) is null) continue;

            ledger.OpenAccount(
                new Account { StrategyId = row.StrategyId, StartingCash = startingCash, RunKind = runKind },
                asOf);
            opened.Add(row.StrategyId);
        }

        return opened;
    }
}
