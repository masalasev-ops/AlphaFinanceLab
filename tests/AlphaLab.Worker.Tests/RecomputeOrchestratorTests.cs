using AlphaLab.Core.Config;
using AlphaLab.Data;
using AlphaLab.Evaluation.Recompute;
using AlphaLab.Worker.Ops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The `replay-recompute` chain's own contract (D106/D117): it archives a markdown report, and it writes
/// NOTHING to the store. The harness's arithmetic is covered by `FX-RecomputeParity` in
/// AlphaLab.Evaluation.Tests; these assert the orchestration around it.
/// </summary>
public class RecomputeOrchestratorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "alphalab-recompute-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _reportDir = Path.Combine(Path.GetTempPath(), "alphalab-recompute-report-" + Guid.NewGuid().ToString("N"));

    public RecomputeOrchestratorTests()
    {
        using var db = Open();
        db.Database.Migrate();
    }

    private AlphaLabDbContext Open() =>
        new(new DbContextOptionsBuilder<AlphaLabDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private RecomputeOrchestrator Orchestrator(AlphaLabDbContext db) =>
        new(db, new GateOptions(), new ArenaOptions { Id = "sp500", DisplayName = "S&P 500" },
            NullLogger<RecomputeOrchestrator>.Instance);

    /// <summary>D117 clause 1 settles §25.5(a) as report-only: the artefact is the whole output. An empty
    /// generation is a legitimate input — the report says so rather than failing.</summary>
    [Fact]
    public void Run_ArchivesAMarkdownReport_UnderTheArenaDirectory()
    {
        using var db = Open();
        var run = Orchestrator(db).Run(RecomputeSpec.Parity, "2026-08-02", _reportDir);

        Assert.True(File.Exists(run.ReportPath));
        Assert.Contains(Path.Combine("sp500", "2026-08-02-recompute-parity.md"), run.ReportPath, StringComparison.Ordinal);

        var text = File.ReadAllText(run.ReportPath);
        Assert.Contains("Report-only — no rows were written", text, StringComparison.Ordinal);
        // The §25.1 limit the harness cannot verify from the store travels WITH the number, not behind it.
        Assert.Contains("`GateOptions` is not as-of resolvable", text, StringComparison.Ordinal);
        Assert.Contains("FX-RecomputeParity", text, StringComparison.Ordinal);
    }

    /// <summary>A rule-change run must NOT read as a parity claim: a difference is its product. The report
    /// says which kind of run it was in its own words, because the same table means opposite things.</summary>
    [Fact]
    public void Run_WithOverrides_ReportsAsARuleChange_AndNamesTheConfirmationSliceRequirement()
    {
        using var db = Open();
        var spec = new RecomputeSpec("gen2",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["gate.alpha_definition"] = "jensen" });

        var run = Orchestrator(db).Run(spec, "2026-08-02", _reportDir);
        var text = File.ReadAllText(run.ReportPath);

        Assert.Contains("scored a **rule change**", text, StringComparison.Ordinal);
        Assert.Contains("confirmation slice", text, StringComparison.Ordinal);
        Assert.Contains("EquityDerived", text, StringComparison.Ordinal);
    }

    /// <summary>D117 clause 1, asserted at the orchestration layer too: the verb that an operator points at
    /// the LIVE store must not be able to write to it.</summary>
    [Fact]
    public void Run_WritesNoRows()
    {
        int[] Counts()
        {
            using var db = Open();
            return [db.OverfittingStatus.Count(), db.GoLiveLog.Count(), db.PowerReports.Count(), db.Config.Count()];
        }

        var before = Counts();
        using (var db = Open()) Orchestrator(db).Run(RecomputeSpec.Parity, "2026-08-02", _reportDir);
        Assert.Equal(before, Counts());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_reportDir)) Directory.Delete(_reportDir, recursive: true); } catch (IOException) { }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The four-way gate verdict, pinned. Getting "the gate would reopen" backwards is the most
    /// consequential thing this report can say — a reader acts on that sentence without re-deriving it —
    /// so the branch is a pure function with a fixture rather than a switch buried in string building.
    /// `+∞` is finding 336's state: no rung reaches the power at the horizon.
    /// </summary>
    [Theory]
    // stored unreachable → recomputed reachable: the change REOPENS the gate
    [InlineData(double.PositiveInfinity, 0.16, "THE GATE WOULD REOPEN")]
    // both unreachable: finding 336 persists
    [InlineData(double.PositiveInfinity, double.PositiveInfinity, "The gate stays CLOSED")]
    // stored reachable → recomputed unreachable: the change CLOSES it — an argument against the change
    [InlineData(0.16, double.PositiveInfinity, "WARNING — this change CLOSES the gate")]
    // both reachable: the level moves, the existence does not
    [InlineData(0.16, 0.08, "moves its level rather than its existence")]
    public void GateVerdict_NamesTheConsequenceInEachDirection(double stored, double recomputed, string expected)
    {
        Assert.Contains(expected, RecomputeOrchestrator.GateVerdict(stored, recomputed), StringComparison.Ordinal);
    }

    /// <summary>
    /// **Finding 344, pinned.** A horizon where both judged cohorts sit within ONE PLANT of the ceiling
    /// cannot discriminate, and the sign of its separation is noise. The live `sustain_evals=4` run produced
    /// exactly that — `anti` 49/50 vs `noedge` 50/50 — and the first version read its −0.02 as a real
    /// starting point and called the move to 0.00 an improvement.
    /// </summary>
    [Fact]
    public void CohortSeparation_TreatsAWithinOnePlantHorizon_AsSaturated_NotAsASignal()
    {
        var saturated = new SeparationAtHorizon("1 year", 252,
            [new CohortFlagRate("anti", 50, 49, 49), new CohortFlagRate("noedge", 50, 50, 49)],
            -0.02, 0.00);
        var discriminating = new SeparationAtHorizon("1 year", 252,
            [new CohortFlagRate("anti", 50, 45, 45), new CohortFlagRate("noedge", 50, 10, 5)],
            0.70, 0.80);

        Assert.True(saturated.Saturated);
        Assert.False(discriminating.Saturated);
        Assert.Equal(0.02, saturated.Resolution!.Value, 6);

        // …and the selector must SKIP the saturated horizon rather than read a verdict from it.
        Assert.Equal("2 years", new CohortSeparationResult(
        [
            saturated,
            discriminating with { Label = "2 years" },
        ], []).Discriminating!.Label);

        // Every horizon saturated ⇒ no verdict is available, which is itself the finding.
        Assert.Null(new CohortSeparationResult([saturated], []).Discriminating);
    }
}
