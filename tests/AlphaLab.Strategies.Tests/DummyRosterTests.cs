using System.Text.Json;
using AlphaLab.Core.Domain;
using AlphaLab.Data.Entities;
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
    /// <summary>The run watermark for <see cref="AsOf"/> (D141: the capital read is now as-of).</summary>
    private const string Wm = "2024-01-02T22:00:00Z";

    [Fact]
    public void Seed_RegistersThreeStrategies_OpensThreeAccounts_WritesStartingCashConfig()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            var ledger = new LedgerStore(db);
            var accounts = new DummyRoster(db, ledger).Seed(AsOf, Wm);

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

            var first = roster.Seed(AsOf, Wm);
            var second = roster.Seed("2024-02-01", "2024-02-01T22:00:00Z"); // a later re-run

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
            new DummyRoster(db, new LedgerStore(db)).Seed(AsOf, Wm);

            var cwJson = db.Strategies.Single(s => s.StrategyId == "buyhold:cw").ExitPolicyJson;
            var ewJson = db.Strategies.Single(s => s.StrategyId == "buyhold:ew").ExitPolicyJson;

            Assert.IsType<ExitPolicy.Never>(JsonSerializer.Deserialize<ExitPolicy>(cwJson, AlphaLabJson.Options));
            var ew = Assert.IsType<ExitPolicy.ScheduledRebalance>(JsonSerializer.Deserialize<ExitPolicy>(ewJson, AlphaLabJson.Options));
            Assert.Equal(21, ew.EveryNDays);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>
    /// THE RULE-8 REGRESSION (D152, finding 422). The reference values are the `config_json` of the three
    /// rows in the LIVE sp500 arena, verbatim.
    ///
    /// <para>**WHY LITERALS AND NOT THE SEEDED ROW ITSELF.** The obvious form of this test — seed a fresh
    /// arena, then assert the registry's plan matches the row `DummyRoster` just wrote — CANNOT FAIL. The
    /// writer serializes `BuyAndHoldModel.CapWeight().Config` and the runner executes
    /// `BuyAndHoldModel.CapWeight()`; one edit moves both, so a fresh arena always agrees with itself.
    /// That is the tautology this whole finding is about, one layer up.</para>
    ///
    /// <para>The divergence is only visible against an arena frozen EARLIER, because `RegisterStrategy`
    /// is idempotent (D17, `:119`): on an existing store an edit to a `Create(...)` default is written
    /// NOWHERE and executed EVERYWHERE — no fork, no `trials_registry` row, no log line, and every
    /// subsequent day judged under parameters the store does not record. Pinning the historical bytes
    /// here reproduces that comparison, so an edit to a default reddens THIS test, which is the
    /// enforcement rule 8 previously lacked. If a fork is ever genuinely intended, it takes a new
    /// `strategy_id` and these literals stay exactly as they are.</para>
    /// </summary>
    [Theory]
    [InlineData("buyhold:cw", """{"seed":0,"selection":{"mode":"top_n","n":1,"min_score":0.6,"max_concurrent":60},"sizing":"equal","params":{},"unregistered":false}""")]
    [InlineData("buyhold:ew", """{"seed":0,"selection":{"mode":"top_n","n":100000,"min_score":0.6,"max_concurrent":60},"sizing":"equal","params":{},"unregistered":false}""")]
    [InlineData("threshold:sma50", """{"seed":0,"selection":{"mode":"threshold","n":40,"min_score":0.6,"max_concurrent":60},"sizing":"equal","params":{"lookback":50},"unregistered":true}""")]
    public void D152_AFreshArenaFreezesWhatTheLiveArenaAlreadyFroze(string strategyId, string liveArenaConfigJson)
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            new DummyRoster(db, new LedgerStore(db)).Seed(AsOf, Wm);

            var row = db.Strategies.Single(s => s.StrategyId == strategyId);
            var fresh = StrategyConfigJson.Read(row.ConfigJson);
            var live = StrategyConfigJson.Read(liveArenaConfigJson);
            Assert.NotNull(fresh);
            Assert.NotNull(live);

            // Compared field by field rather than byte by byte ON PURPOSE: D152 also puts the writer
            // through the canonicalizer, so the fresh bytes are legitimately in a different ORDER from
            // the live arena's pre-canonical ones while recording the identical parameters. A byte
            // assertion here would fail for a reason that has nothing to do with rule 8.
            Assert.Equal(live!.Seed, fresh!.Seed);
            Assert.Equal(live.Sizing, fresh.Sizing);
            Assert.Equal(live.Selection.Mode, fresh.Selection.Mode);
            Assert.Equal(live.Selection.N, fresh.Selection.N);
            Assert.Equal(live.Selection.MinScore, fresh.Selection.MinScore);
            Assert.Equal(live.Selection.MaxConcurrent, fresh.Selection.MaxConcurrent);
            Assert.Equal(
                live.Params.OrderBy(p => p.Key, StringComparer.Ordinal).ToList(),
                fresh.Params.OrderBy(p => p.Key, StringComparer.Ordinal).ToList());

            // And the runner agrees with the row the live arena holds — the join D152 closes.
            var plan = StrategyRegistry.ForRow(new StrategyRow
            {
                StrategyId = strategyId, Family = "passive", ConfigJson = liveArenaConfigJson,
                ExitPolicyJson = "{}", CreatedOn = AsOf, Status = "candidate",
            });
            Assert.NotNull(plan);
        }
        finally { TestDb.Delete(path); }
    }

    /// <summary>
    /// D152 / finding 423: the stored bytes are in D133's canonical form, so `Write(Read(stored))` equals
    /// `stored`. `DummyRoster` used to write a raw typed serialize, which carried insertion order and
    /// omitted `frozen`/`frozen_sets`/`horizon` — so the round-trip property D133's row claims for every
    /// frozen row was false for all three of the rows that actually trade.
    ///
    /// <para>Asserted against the STORED BYTES, not `Write` against `Write`: the existing
    /// `D133_ConfigJson_RoundTripsEveryFrozenRow` compares `Write(original)` with `Write(read)`, which is
    /// true of any canonicalizer whatsoever and therefore cannot detect this. Fresh arenas only —
    /// `RegisterStrategy` never rewrites an existing row (D17).</para>
    /// </summary>
    [Fact]
    public void D152_TheFrozenBytesAreCanonical_SoAReadBackReSerializesIdentically()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            new DummyRoster(db, new LedgerStore(db)).Seed(AsOf, Wm);

            var rows = db.Strategies.OrderBy(s => s.StrategyId).ToList();
            Assert.Equal(3, rows.Count);

            foreach (var row in rows)
            {
                var read = StrategyConfigJson.Read(row.ConfigJson);
                Assert.NotNull(read);
                Assert.Equal(row.ConfigJson, StrategyConfigJson.Write(read!));
            }
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

            var accounts = new DummyRoster(db, new LedgerStore(db)).Seed(AsOf, Wm);
            Assert.All(accounts, a => Assert.Equal(250_000m, a.StartingCash));
            Assert.Single(db.Config.Where(c => c.Key == DummyRoster.StartingCashConfigKey).ToList()); // unchanged
        }
        finally { TestDb.Delete(path); }
    }
}
