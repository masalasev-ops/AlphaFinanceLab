namespace AlphaLab.Data.Tests;

/// <summary>
/// P20's GUARD. The attribute alone leaves a landmine: `ClearAllPools()` stays in the code and stays
/// PROCESS-GLOBAL, so a new test project added without
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c> silently reintroduces the
/// race — and it would present as a fresh mystery flake rather than as P20 returning, costing the same
/// diagnosis twice.
///
/// **Why it scans SOURCE rather than reflecting over loaded assemblies.** The failure this guards
/// against is a project that does not exist yet. A reflection check can only see assemblies this test
/// project references, and a new sibling test project would reference nothing here — so it would pass
/// while being exactly the thing that is broken. The filesystem is where the answer lives.
///
/// **Why every test project and not only the three with call sites.** `ClearAllPools()` is
/// process-global, so a project with no site of its own is still a VICTIM of one. Uniformity is also
/// what makes the rule checkable: "every test assembly" is a claim a guard can verify, whereas "every
/// test assembly that transitively touches SQLite" is one it cannot.
/// </summary>
public class TestParallelizationGuardTests
{
    private const string Attribute = "DisableTestParallelization = true";

    [Fact]
    public void P20_EveryTestAssemblyDisablesParallelization()
    {
        var testsRoot = Path.Combine(FindRepoRoot(), "tests");
        Assert.True(Directory.Exists(testsRoot), $"tests root not found at {testsRoot}");

        var projects = Directory.EnumerateDirectories(testsRoot)
            .Where(d => Directory.EnumerateFiles(d, "*.csproj").Any())
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        // A guard that found nothing to check would pass forever while proving nothing.
        Assert.True(projects.Count >= 7, $"expected at least 7 test projects under {testsRoot}, found {projects.Count}");

        var missing = projects
            .Where(p => !DeclaresTheAttribute(p))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(missing.Count == 0,
            "These test projects do not disable xUnit parallelization: " + string.Join(", ", missing) + ". " +
            "SqliteConnection.ClearAllPools() is PROCESS-GLOBAL and is called from per-test cleanup across " +
            "the suite, so a parallelized assembly can have its connections disposed mid-test by another " +
            "class's teardown (P20 / finding 387, measured 1-in-3 on clean main). Add a TestParallelization.cs " +
            "carrying [assembly: CollectionBehavior(DisableTestParallelization = true)] — or, if you are here " +
            "because you want the parallelism back, do the Pooling=False pass PROGRESS's P20 entry names as " +
            "the preferred long-term fix, which deletes the idiom rather than guarding it.");
    }

    private static bool DeclaresTheAttribute(string projectDir) =>
        Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Any(f => File.ReadAllText(f).Contains(Attribute, StringComparison.Ordinal));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "AlphaLab.slnx")) &&
               !File.Exists(Path.Combine(dir.FullName, "AlphaLab.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root (AlphaLab.slnx) not found");
    }
}
