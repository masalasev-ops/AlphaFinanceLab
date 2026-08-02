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
}
