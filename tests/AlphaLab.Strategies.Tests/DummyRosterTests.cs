using System.Text.Json;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Json;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Services;

namespace AlphaLab.Strategies.Tests;

/// <summary>
/// DummyRoster seeds the three Phase-2 strategies + their accounts and writes Accounts.StartingCash as
/// a versioned config row (finding K). Idempotent: a re-run adds nothing.
/// </summary>
public class DummyRosterTests
{
    private const string AsOf = "2024-01-02";

    [Fact]
    public void Seed_RegistersThreeStrategies_OpensThreeAccounts_WritesStartingCashConfig()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var ledger = new LedgerStore(db);
            var accounts = new DummyRoster(db, ledger).Seed(AsOf);

            // Three accounts, each opened at $100,000.
            Assert.Equal(3, accounts.Count);
            Assert.All(accounts, a => Assert.Equal(100_000m, a.StartingCash));
            Assert.All(accounts, a => Assert.Equal(RunKind.Live, a.RunKind));

            // Three strategies with the right status + config.
            Assert.Equal(3, db.Strategies.Count());
            var cw = db.Strategies.Single(s => s.StrategyId == "buyhold:cw");
            var ew = db.Strategies.Single(s => s.StrategyId == "buyhold:ew");
            var th = db.Strategies.Single(s => s.StrategyId == "threshold:sma50");
            Assert.Equal("baseline", cw.Status);
            Assert.Equal("baseline", ew.Status);
            Assert.Equal("candidate", th.Status);
            Assert.Null(cw.HoldingHorizonDays); // ToNextRebalance has no day count

            // StartingCash is a versioned config row (v1 = the $100k default), the Regime.ProxySecurityId precedent.
            var cfg = db.Config.Single(c => c.Key == DummyRoster.StartingCashConfigKey);
            Assert.Equal(1, cfg.Version);
            Assert.Equal("100000", cfg.ValueJson);

            // Each account got its opening deposit (the curve reconciles from events, not a bare balance).
            foreach (var a in accounts)
            {
                var deposit = Assert.Single(ledger.GetCashEvents(a.AccountId, RunKind.Live));
                Assert.Equal(CashEventType.Deposit, deposit.Type);
                Assert.Equal(100_000m, deposit.Amount);
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void Seed_IsIdempotent_NoDuplicatesOnReRun()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var ledger = new LedgerStore(db);
            var roster = new DummyRoster(db, ledger);

            var first = roster.Seed(AsOf);
            var second = roster.Seed("2024-02-01"); // a later re-run

            Assert.Equal(3, db.Strategies.Count());                                   // not six
            Assert.Equal(3, ledger.GetAccounts(RunKind.Live).Count);                  // not six
            Assert.Single(db.Config.Where(c => c.Key == DummyRoster.StartingCashConfigKey).ToList()); // still one version
            Assert.Equal(first.Select(a => a.AccountId), second.Select(a => a.AccountId)); // same accounts reused
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void Seed_ExitPolicyJson_RoundTripsThePolymorphicShape()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            new DummyRoster(db, new LedgerStore(db)).Seed(AsOf);

            var cwJson = db.Strategies.Single(s => s.StrategyId == "buyhold:cw").ExitPolicyJson;
            var ewJson = db.Strategies.Single(s => s.StrategyId == "buyhold:ew").ExitPolicyJson;

            Assert.IsType<ExitPolicy.Never>(JsonSerializer.Deserialize<ExitPolicy>(cwJson, AlphaLabJson.Options));
            var ew = Assert.IsType<ExitPolicy.ScheduledRebalance>(JsonSerializer.Deserialize<ExitPolicy>(ewJson, AlphaLabJson.Options));
            Assert.Equal(21, ew.EveryNDays);
        }
        finally { TestDb.Delete(path); }
    }

    [Fact]
    public void ResolveStartingCash_ReadsExistingConfigRow_RatherThanRewriting()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            // A pre-existing operator-set value: accounts must open at THIS, not the default.
            db.Config.Add(new AlphaLab.Data.Entities.ConfigRow
            {
                Key = DummyRoster.StartingCashConfigKey, ValueJson = "250000", Version = 1,
                ChangedOn = "2023-01-01", Reason = "operator override",
            });
            db.SaveChanges();

            var accounts = new DummyRoster(db, new LedgerStore(db)).Seed(AsOf);
            Assert.All(accounts, a => Assert.Equal(250_000m, a.StartingCash));
            Assert.Single(db.Config.Where(c => c.Key == DummyRoster.StartingCashConfigKey).ToList()); // unchanged
        }
        finally { TestDb.Delete(path); }
    }
}
