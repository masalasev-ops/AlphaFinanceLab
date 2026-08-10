using AlphaLab.Core.Ledger;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;

namespace AlphaLab.Strategies.Tests;

/// <summary>
/// FX-AdmittedCandidateOpensAccount (6.2) — the second half of the lifecycle seam.
///
/// `CandidateFactory` writes a `strategies` row and stops, and `DailyPipeline` iterates ACCOUNTS, so an
/// admitted strategy with no account was never even reached to be warned about: it could pass the D89
/// gate, spend a trial that raises the Bonferroni floor for every candidate after it, and never trade.
/// </summary>
public class StrategyRosterTests
{
    private const string AsOf = "2024-01-02";
    /// <summary>The run watermark for <see cref="AsOf"/> (D141: the capital read is now as-of).</summary>
    private const string Wm = "2024-01-02T22:00:00Z";

    private static StrategyRow Row(string id, string status) => new()
    {
        StrategyId = id, Family = "passive", ConfigJson = "{}", ExitPolicyJson = "{}",
        CreatedOn = AsOf, Status = status,
    };

    [Fact]
    public void FX_AdmittedCandidateOpensAccount()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var ledger = new LedgerStore(db);

            // Admitted, runnable, and with NO account — exactly the state CandidateFactory leaves behind.
            db.Strategies.Add(Row("threshold:sma50", "candidate"));
            db.SaveChanges();
            Assert.Empty(ledger.GetAccounts(RunKind.Live));

            var opened = new StrategyRoster(db, ledger).OpenMissingAccounts(AsOf, Wm);

            Assert.Equal(["threshold:sma50"], opened);
            var account = Assert.Single(ledger.GetAccounts(RunKind.Live));
            Assert.Equal("threshold:sma50", account.StrategyId);
            Assert.Equal(100_000m, account.StartingCash);   // the versioned Accounts.StartingCash row
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void OpeningIsIdempotent_ARerunAddsNothing()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var ledger = new LedgerStore(db);
            db.Strategies.Add(Row("threshold:sma50", "candidate"));
            db.SaveChanges();

            var first = new StrategyRoster(db, ledger).OpenMissingAccounts(AsOf, Wm);
            var second = new StrategyRoster(db, ledger).OpenMissingAccounts(AsOf, Wm);

            Assert.Single(first);
            Assert.Empty(second);                                  // nothing to open the second time
            Assert.Single(ledger.GetAccounts(RunKind.Live));       // and no duplicate account
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>
    /// Rule 10. An admitted row this build cannot construct gets NO account: opening one would create an
    /// account the funnel skips every single day — a strategy that looks live in the ledger and never
    /// trades, which is a worse lie than the missing account it replaced.
    /// </summary>
    [Fact]
    public void AnUnrunnableAdmittedRow_GetsNoAccount()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var ledger = new LedgerStore(db);
            db.Strategies.Add(new StrategyRow
            {
                StrategyId = "momentum:L126:K21:N40", Family = "momentum", ConfigJson = "{}",
                ExitPolicyJson = "{}", CreatedOn = AsOf, Status = "candidate",
            });
            db.SaveChanges();

            Assert.Empty(new StrategyRoster(db, ledger).OpenMissingAccounts(AsOf, Wm));
            Assert.Empty(ledger.GetAccounts(RunKind.Live));
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>A retirement is an ending: a retired row never gets a fresh account.</summary>
    [Fact]
    public void RetiredStrategies_GetNoAccount()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var ledger = new LedgerStore(db);
            db.Strategies.Add(Row("threshold:sma50", "retired"));
            db.SaveChanges();

            Assert.Empty(new StrategyRoster(db, ledger).OpenMissingAccounts(AsOf, Wm));
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>
    /// FORWARD ONLY. A replay opens its own accounts (D37), and generation 2 — the frozen calibration
    /// the D106/D117 harness reproduces — was built from exactly the roster the replay seeds. Opening
    /// candidate accounts inside a replay would change what trades there and put FX-RecomputeParity at
    /// risk for no gain, since only a forward track judges a strategy (rule 1).
    /// </summary>
    [Fact]
    public void ReplayOpensNothing_TheFrozenGenerationIsNotDisturbed()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var ledger = new LedgerStore(db);
            db.Strategies.Add(Row("threshold:sma50", "candidate"));
            db.SaveChanges();

            Assert.Empty(new StrategyRoster(db, ledger).OpenMissingAccounts(AsOf, Wm, RunKind.Replay));
            Assert.Empty(ledger.GetAccounts(RunKind.Replay));
        }
        finally { TestDb.Delete(path); }
    }
}
