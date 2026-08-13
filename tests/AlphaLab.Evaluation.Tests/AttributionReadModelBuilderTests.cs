using AlphaLab.Core.Config;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Evaluation.ReadModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// The D41 attribution panel (checkpoint 6.6). UX tests are read-model unit tests, never browser tests
/// (rule 18) — everything the client renders is a field here.
/// </summary>
public class AttributionReadModelBuilderTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"alphalab-attr-{Guid.NewGuid():N}.db");

    private static AlphaLabDbContext NewContext(string path) =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={path}").Options);

    private static void TryDelete(string path)
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    private const string Strategy = "momentum:L126:K21:N40";
    private static readonly string[] Factors = ["MKT_RF", "SMB", "HML", "UMD", "RMW"];

    private static string Day(int i) => DateOnly.FromDateTime(new DateTime(2026, 1, 1).AddDays(i)).ToString("yyyy-MM-dd");

    /// <summary>A store with a forward run, a strategy account, `days` of equity, and (optionally) a full
    /// factor panel over the same sessions. Loadings are PLANTED so the fit has a known answer.</summary>
    private static AlphaLabDbContext Seed(
        string path, int days, bool withFactors = true, int factorDays = -1, double mktLoading = 0.9)
    {
        using (var db0 = NewContext(path)) db0.Database.Migrate();
        var db = NewContext(path);

        db.Runs.Add(new RunRow
        {
            RunId = 1, AsOf = Day(days), RunKind = "live", Status = "ok",
            Watermark = $"{Day(days)}T22:00:00Z", StartedAt = $"{Day(days)}T21:00:00Z",
        });
        db.Strategies.Add(new AlphaLab.Data.Entities.StrategyRow
        {
            StrategyId = Strategy, Family = "momentum", ConfigJson = "{}", ExitPolicyJson = "{}",
            CreatedOn = Day(0), Status = "live",
        });
        db.Accounts.Add(new AccountRow
        {
            AccountId = 1, StrategyId = Strategy, RunKind = "live", StartingCash = 100000m,
        });

        var rng = new Random(7);
        var equity = 100000.0;
        var factorRows = factorDays < 0 ? days : factorDays;

        for (var i = 0; i <= days; i++)
        {
            var d = Day(i);
            var mkt = (rng.NextDouble() - 0.5) * 0.02;
            var smb = (rng.NextDouble() - 0.5) * 0.01;
            var hml = (rng.NextDouble() - 0.5) * 0.01;
            var umd = (rng.NextDouble() - 0.5) * 0.01;
            var rmw = (rng.NextDouble() - 0.5) * 0.01;
            var rf = 0.00012;

            if (i > 0)
            {
                // The planted truth: r_s = rf + 0.9·MKT + 0.3·UMD + noise ⇒ β_mkt ≈ 0.9, β_umd ≈ 0.3.
                var r = rf + mktLoading * mkt + 0.3 * umd + (rng.NextDouble() - 0.5) * 0.0004;
                equity *= 1.0 + r;
                db.EquityCurve.Add(new EquityCurveRow
                {
                    AccountId = 1, AsOf = d, RunKind = "live", Equity = (decimal)equity,
                });
            }
            else
            {
                db.EquityCurve.Add(new EquityCurveRow
                {
                    AccountId = 1, AsOf = d, RunKind = "live", Equity = (decimal)equity,
                });
            }

            if (withFactors && i <= factorRows)
            {
                db.FactorReturns.Add(new FactorReturnRow { Date = d, Factor = "MKT_RF", Value = mkt });
                db.FactorReturns.Add(new FactorReturnRow { Date = d, Factor = "SMB", Value = smb });
                db.FactorReturns.Add(new FactorReturnRow { Date = d, Factor = "HML", Value = hml });
                db.FactorReturns.Add(new FactorReturnRow { Date = d, Factor = "UMD", Value = umd });
                db.FactorReturns.Add(new FactorReturnRow { Date = d, Factor = "RMW", Value = rmw });
                db.FactorReturns.Add(new FactorReturnRow { Date = d, Factor = "RF", Value = rf });
            }
        }

        db.SaveChanges();
        return db;
    }

    private static AttributionReadModelBuilder B(AlphaLabDbContext db) => new(db, new GateOptions());

    // ---------- the fit ----------

    [Fact]
    public void FR13_D41_RecoversThePlantedLoadings_AndReportsAllFiveFactors()
    {
        var p = TempDb();
        try
        {
            using var db = Seed(p, 400);
            var m = B(db).Build(Strategy);

            Assert.True(m.HasFit, m.Unavailable);
            Assert.Null(m.Unavailable);
            Assert.Equal(Factors, m.Loadings.Select(l => l.Factor));   // §1.4's order, exactly

            var mkt = m.Loadings.Single(l => l.Factor == "MKT_RF");
            var umd = m.Loadings.Single(l => l.Factor == "UMD");
            Assert.True(Math.Abs(mkt.Beta - 0.9) < 0.05, $"β_mkt expected ≈0.9, got {mkt.Beta:F4}");
            Assert.True(Math.Abs(umd.Beta - 0.3) < 0.05, $"β_umd expected ≈0.3, got {umd.Beta:F4}");
            Assert.All(m.Loadings, l => Assert.True(l.StdError > 0));
            Assert.Equal(21, m.Lag);   // the derived cap, not an authored constant
        }
        finally { TryDelete(p); }
    }

    /// <summary>Rule 18: the client renders, it does not compute. Every displayed string is a field.</summary>
    [Fact]
    public void FR13_D41_TheClientRendersFieldsRatherThanComputingThem()
    {
        var p = TempDb();
        try
        {
            using var db = Seed(p, 400);
            var m = B(db).Build(Strategy);

            Assert.NotNull(m.AlphaFormatted);
            Assert.EndsWith("%/yr", m.AlphaFormatted);
            Assert.All(m.Loadings, l => Assert.False(string.IsNullOrWhiteSpace(l.Formatted)));
            Assert.NotNull(m.Stamp.RunId);
            Assert.NotNull(m.Stamp.Watermark);   // D60: every read-model stamped
        }
        finally { TryDelete(p); }
    }

    // ---------- the lag note ----------

    /// <summary>
    /// D41's LITERAL wording: *"the panel states 'factor data through &lt;date&gt;'"*. Pinned to the exact
    /// phrase because a client that invents its own is no longer rendering the honesty verbatim.
    /// </summary>
    [Fact]
    public void FR13_D41_TheLagNote_CarriesTheRegistersLiteralWording()
    {
        var p = TempDb();
        try
        {
            using var db = Seed(p, 400);
            var m = B(db).Build(Strategy);

            Assert.Equal($"factor data through {m.FactorDataThrough}", m.LagNote);
            Assert.StartsWith("factor data through ", m.LagNote);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", m.FactorDataThrough!);
        }
        finally { TryDelete(p); }
    }

    /// <summary>
    /// **finding 447 — THE MOCKUP CONTRADICTS EVERY SPECIFICATION AND MUST NOT BE COPIED.**
    /// `docs/alphalab_ux_mockups.html` shows this feed as `'2-day lag · expected'` / `'current · 2d lag ok'`.
    /// INTEGRATIONS §3, D83 and DESIGN_IMPROVEMENTS §1.4 all say **weeks**. A freshness verdict built from
    /// the mockup sits permanently amber under the real cadence, so this read-model publishes the
    /// THROUGH-DATE and NO verdict — the honest statement is "here is how current the data is", not "here
    /// is whether that is acceptable", because the latter needs a threshold no document states.
    /// </summary>
    [Fact]
    public void FR13_D41_ThePanelPublishesNoFreshnessVerdict_finding447()
    {
        var p = TempDb();
        try
        {
            // Factors stop 30 days before the track ends — weeks of lag, the REAL cadence.
            using var db = Seed(p, 400, factorDays: 370);
            var m = B(db).Build(Strategy);

            Assert.True(m.HasFit, m.Unavailable);
            Assert.Equal(Day(370), m.FactorDataThrough);

            // The DTO has no "fresh"/"stale"/"ok" field at all — the absence is the assertion.
            var names = typeof(AttributionReadModel).GetProperties().Select(x => x.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Fresh", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("Stale", StringComparison.OrdinalIgnoreCase));

            // And a lag of weeks is NOT treated as a failure: the fit still exists.
            Assert.NotNull(m.AlphaAnnualized);
        }
        finally { TryDelete(p); }
    }

    // ---------- the unavailable reasons, each distinct ----------

    [Fact]
    public void FR13_D41_AShortTrack_IsInsufficientTrack_NotAnEmptyPanel()
    {
        var p = TempDb();
        try
        {
            using var db = Seed(p, 100);   // < 252
            var m = B(db).Build(Strategy);

            Assert.False(m.HasFit);
            Assert.Equal(AttributionReadModel.UnavailableInsufficientTrack, m.Unavailable);
        }
        finally { TryDelete(p); }
    }

    /// <summary>"No factor feed" and "not enough track" are different operator problems and must not
    /// collapse into one blank panel.</summary>
    [Fact]
    public void FR13_D41_NoFactorData_IsItsOwnReason()
    {
        var p = TempDb();
        try
        {
            using var db = Seed(p, 400, withFactors: false);
            var m = B(db).Build(Strategy);

            Assert.False(m.HasFit);
            Assert.Equal(AttributionReadModel.UnavailableNoFactorData, m.Unavailable);
            Assert.Null(m.LagNote);   // there is no through-date to state
        }
        finally { TryDelete(p); }
    }

    [Fact]
    public void FR13_D41_AFactorFeedTooShortForTheWindow_IsAGap_NotAShortTrack()
    {
        var p = TempDb();
        try
        {
            using var db = Seed(p, 400, factorDays: 100);   // plenty of track, too little factor data
            var m = B(db).Build(Strategy);

            Assert.False(m.HasFit);
            Assert.Equal(AttributionReadModel.UnavailableFactorDataGap, m.Unavailable);
            Assert.NotNull(m.LagNote);   // the through-date is still stated — that IS the diagnosis
        }
        finally { TryDelete(p); }
    }

    [Fact]
    public void FR13_D41_AnUnknownStrategy_IsNoRunYet_NotAFabricatedPanel()
    {
        var p = TempDb();
        try
        {
            using var db = Seed(p, 400);
            var m = B(db).Build("does-not-exist");
            Assert.Equal(ReadModelStampStatus.NoRunYet, m.Stamp.Status);
            Assert.False(m.HasFit);
        }
        finally { TryDelete(p); }
    }

    /// <summary>Rule 1 / D149: a replay fixture is never rendered as a forward card. Routed through
    /// `ForwardVisibility`, so this cannot be satisfied by a predicate copied into this builder.</summary>
    [Fact]
    public void FR13_D41_APlant_HasNoForwardAttributionPanel()
    {
        var p = TempDb();
        try
        {
            using var db = Seed(p, 400);
            db.Strategies.Add(new AlphaLab.Data.Entities.StrategyRow
            {
                StrategyId = "plant:edge:seed1", Family = "plant", ConfigJson = "{}", ExitPolicyJson = "{}",
                CreatedOn = Day(0), Status = "live",
            });
            db.SaveChanges();

            var m = B(db).Build("plant:edge:seed1");
            Assert.Equal(ReadModelStampStatus.NoRunYet, m.Stamp.Status);
            Assert.False(m.HasFit);
        }
        finally { TryDelete(p); }
    }

    [Fact]
    public void FR13_D41_CoverageIsPublished_BesideTheFit()
    {
        var p = TempDb();
        try
        {
            using var db = Seed(p, 400, factorDays: 370);
            var m = B(db).Build(Strategy);

            Assert.True(m.CoveredSessions > 0);
            Assert.True(m.TotalSessions >= m.CoveredSessions);
            Assert.True(m.CoveredSessions < m.TotalSessions,
                "this fixture deliberately has a factor gap, so coverage must be strictly partial");
        }
        finally { TryDelete(p); }
    }
}
