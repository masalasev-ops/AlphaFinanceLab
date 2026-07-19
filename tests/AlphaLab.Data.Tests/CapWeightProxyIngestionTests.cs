using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;

namespace AlphaLab.Data.Tests;

/// <summary>
/// The cap-weight benchmark ETF proxy ingestion (STRATEGY_CATALOG §5.1 / D26/D27). Unlike the regime proxy,
/// the proxy is a real ETF fetched through the ordinary member path, so this service only resolves identity
/// + writes the versioned <c>Benchmark.CapWeightProxySecurityId</c> pointer (bars/divs/splits land via
/// BackfillSecurityStep). The symbol + key arrive as strings (Data cannot reference AlphaLab.Strategies).
/// </summary>
public class CapWeightProxyIngestionTests
{
    private const string AsOf = "2026-07-13";
    private const string ConfigKey = "Benchmark.CapWeightProxySecurityId"; // == AlphaLab.Strategies.CapWeightProxy.ProxySecurityIdConfigKey

    // ---- The target factory splits a fully-qualified EODHD symbol into the BARE ticker + exchange the ----
    // ---- member fetch path expects (EodhdMarketDataProvider appends the suffix), and fails closed otherwise.
    [Fact]
    public void FromEodhdSymbol_SplitsTickerAndExchange()
    {
        var target = CapWeightProxyTarget.FromEodhdSymbol("OEF.US", ConfigKey, "oef_csv");
        Assert.Equal("OEF", target.Symbol);   // BARE — not "OEF.US" (else the fetch URL is OEF.US.US)
        Assert.Equal("US", target.Exchange);
        Assert.Equal(ConfigKey, target.ConfigKey);
        Assert.Equal("oef_csv", target.Source);
    }

    [Theory]
    [InlineData("OEF")]    // no exchange
    [InlineData("OEF.")]   // trailing dot, empty exchange
    [InlineData(".US")]    // leading dot, empty ticker
    public void FromEodhdSymbol_NotFullyQualified_FailsClosed(string symbol)
    {
        Assert.Throws<ArgumentException>(() => CapWeightProxyTarget.FromEodhdSymbol(symbol, ConfigKey, "oef_csv"));
    }

    // ---- Resolve identity + persist the pointer as a versioned config row (idempotent) ----
    [Fact]
    public void ResolveProxySecurityId_RegistersBareEtf_AndWritesVersionedConfig_Idempotently()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            long id;
            using (var db = TestDb.Open(path))
            {
                id = new CapWeightProxyIngestion(db).ResolveProxySecurityId("OEF", "US", ConfigKey, AsOf, "oef_csv");
            }
            using (var db = TestDb.Open(path))
            {
                // Registered as the BARE ticker so the forward member-path fetch URL (Sym => OEF.US) is correct.
                var sec = db.Securities.Single(s => s.SecurityId == id);
                Assert.Equal("OEF", sec.CurrentSymbol);
                Assert.Equal("US", sec.Exchange);
                // A proper ticker_history alias exists (a future ETF ticker change is representable).
                Assert.Contains(db.TickerHistory.ToList(), t => t.SecurityId == id && t.Symbol == "OEF" && t.ValidTo == null);

                var cfg = Assert.Single(db.Config.Where(c => c.Key == ConfigKey).ToList());
                Assert.Equal(1, cfg.Version);
                Assert.Equal(id.ToString(), cfg.ValueJson);
            }
            using (var db = TestDb.Open(path))
            {
                // Re-resolve is idempotent: same id, NO new config version.
                var again = new CapWeightProxyIngestion(db).ResolveProxySecurityId("OEF", "US", ConfigKey, "2026-07-14", "oef_csv");
                Assert.Equal(id, again);
                Assert.Single(db.Config.Where(c => c.Key == ConfigKey).ToList());
            }
        }
        finally { TestDb.Delete(path); }
    }

    // A later resolve carrying an EARLIER asOf (a re-run) must reuse the same id and mint NO duplicate
    // security — the CurrentSymbol lookup is asOf-independent, closing the fail-open the ticker_history path
    // would open (asOf < valid_from => null => a second identity orphaning the backfilled bars).
    [Fact]
    public void ResolveProxySecurityId_DecreasingAsOf_SameId_NoDuplicate()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            long first, earlier;
            using (var db = TestDb.Open(path)) first = new CapWeightProxyIngestion(db).ResolveProxySecurityId("OEF", "US", ConfigKey, "2026-07-13", "oef_csv");
            using (var db = TestDb.Open(path)) earlier = new CapWeightProxyIngestion(db).ResolveProxySecurityId("OEF", "US", ConfigKey, "2020-01-01", "oef_csv"); // earlier
            using (var db = TestDb.Open(path))
            {
                Assert.Equal(first, earlier);
                Assert.Single(db.Securities.Where(s => s.CurrentSymbol == "OEF").ToList()); // exactly one identity
                Assert.Single(db.Config.Where(c => c.Key == ConfigKey).ToList());
            }
        }
        finally { TestDb.Delete(path); }
    }

    // A pre-existing config value change appends version+1 and leaves the prior row byte-unchanged (append-only).
    [Fact]
    public void ResolveProxySecurityId_ValueChange_AppendsVersion_NeverMutatesPrior()
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using (var db = TestDb.Open(path))
            {
                db.Config.Add(new ConfigRow { Key = ConfigKey, ValueJson = "999", Version = 1, ChangedOn = "2020-01-01", Reason = "seed" });
                db.SaveChanges();
            }
            long id;
            using (var db = TestDb.Open(path)) id = new CapWeightProxyIngestion(db).ResolveProxySecurityId("OEF", "US", ConfigKey, AsOf, "oef_csv");
            using (var db = TestDb.Open(path))
            {
                var rows = db.Config.Where(c => c.Key == ConfigKey).ToList();
                Assert.Equal(2, rows.Count);
                Assert.Equal("999", rows.Single(r => r.Version == 1).ValueJson);   // prior row untouched
                Assert.Equal(id.ToString(), rows.Single(r => r.Version == 2).ValueJson); // MAX(version) => the new id
            }
        }
        finally { TestDb.Delete(path); }
    }

    [Theory]
    [InlineData("", "US")]
    [InlineData("OEF", "")]
    public void ResolveProxySecurityId_BlankArgs_FailClosed(string symbol, string exchange)
    {
        var path = TestDb.CreateMigrated();
        try
        {
            using var db = TestDb.Open(path);
            Assert.Throws<ArgumentException>(() =>
                new CapWeightProxyIngestion(db).ResolveProxySecurityId(symbol, exchange, ConfigKey, AsOf, "oef_csv"));
        }
        finally { TestDb.Delete(path); }
    }
}
