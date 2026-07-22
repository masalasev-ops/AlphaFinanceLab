using System.Diagnostics;
using System.Text;

namespace AlphaLab.Worker.Tests;

/// <summary>
/// The two operator scripts from checkpoint 3.5.3 (FR-25 / RUNBOOK section 3): the off-site backup
/// copy and the nightly-launch task emitter. Neither touches the database.
///
/// These shell out to a real PowerShell, because the thing worth testing about a script is that the
/// script runs — a C# reimplementation of its logic would pass while the .ps1 had a parse error. They
/// SKIP (rather than fail) when no PowerShell is on PATH, so the suite stays portable; both CI legs
/// have pwsh, so coverage is real where it counts.
/// </summary>
public class OpsScriptTests
{
    [Fact]
    public void FR25_BackupOffsite_CopiesTheLatestBackup_AndVerifiesIt()
    {
        if (PowerShell() is not { } shell) return;   // no PowerShell here — covered on CI

        using var fixture = new ScriptFixture();
        // Three dated backups; the newest BY FILENAME is the one that must travel. The older files are
        // given NEWER mtimes on purpose: picking by mtime would choose the wrong one, and the script
        // must agree with LocalBackup's filename-date rule instead.
        var oldest = fixture.WriteBackup("alphalab-2026-07-17.db", "seventeen");
        var newest = fixture.WriteBackup("alphalab-2026-07-19.db", "nineteen-the-real-latest");
        var middle = fixture.WriteBackup("alphalab-2026-07-18.db", "eighteen");
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(middle, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddDays(-5));

        var (exit, output) = Run(shell, fixture.Script("backup-offsite.ps1"),
            "-Arena", "sp500",
            "-Destination", fixture.Destination,
            "-BackupDirectory", fixture.BackupDirectory);

        Assert.True(exit == 0, output);
        Assert.Contains("VERIFIED", output, StringComparison.Ordinal);

        var copied = Path.Combine(fixture.Destination, "alphalab-2026-07-19.db");
        Assert.True(File.Exists(copied), $"expected the newest-by-filename backup at {copied}. Output:\n{output}");
        Assert.Equal(File.ReadAllText(newest), File.ReadAllText(copied));
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "alphalab-2026-07-18.db")));
    }

    [Fact]
    public void FR25_BackupOffsite_WithNoBackupsPresent_FailsLoudly()
    {
        if (PowerShell() is not { } shell) return;

        using var fixture = new ScriptFixture();   // backups dir exists but is empty

        var (exit, output) = Run(shell, fixture.Script("backup-offsite.ps1"),
            "-Arena", "sp500",
            "-Destination", fixture.Destination,
            "-BackupDirectory", fixture.BackupDirectory);

        // Rule 10: never a silent no-op. An off-site routine that "succeeds" without copying anything
        // is worse than none, because it also removes the operator's reason to look.
        Assert.True(exit != 0, "expected a non-zero exit when there is nothing to copy. Output:\n" + output);
        // Assert the reason on NORMALIZED output: the phrase must survive whichever shell ran it.
        // PowerShell 7 decorates and re-wraps error records, so an un-normalized substring match
        // passes under Windows PowerShell 5.1 and fails under pwsh purely on console width.
        Assert.Contains("Nothing to copy off-machine", Normalize(output), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(fixture.Destination));
    }

    [Fact]
    public void FR25_BackupOffsite_WithAMissingDestination_FailsLoudly()
    {
        if (PowerShell() is not { } shell) return;

        using var fixture = new ScriptFixture();
        fixture.WriteBackup("alphalab-2026-07-19.db", "content");

        // -Destination is [Parameter(Mandatory)]; non-interactively that is an error, not a prompt.
        var (exit, output) = Run(shell, fixture.Script("backup-offsite.ps1"),
            "-Arena", "sp500",
            "-BackupDirectory", fixture.BackupDirectory);

        Assert.True(exit != 0, "expected a non-zero exit with no -Destination. Output:\n" + output);
    }

    [Fact]
    public void FR25_RegisterNightly_EmitsAWellFormedTask_AndInstallsNothingByDefault()
    {
        if (PowerShell() is not { } shell) return;

        using var fixture = new ScriptFixture();

        var (exit, output) = Run(shell, fixture.Script("register-nightly-backup.ps1"), "-Arena", "sp500");

        Assert.True(exit == 0, output);

        // Assert against the EMITTED COMMANDS, not the whole transcript: the surrounding prose is for
        // the operator, and only these lines get registered on their machine.
        var action = output.Split('\n').SingleOrDefault(l => l.Contains("New-ScheduledTaskAction", StringComparison.Ordinal));
        var trigger = output.Split('\n').SingleOrDefault(l => l.Contains("New-ScheduledTaskTrigger", StringComparison.Ordinal));
        var register = output.Split('\n').SingleOrDefault(l => l.Contains("Register-ScheduledTask", StringComparison.Ordinal));
        Assert.NotNull(action);
        Assert.NotNull(trigger);
        Assert.NotNull(register);

        Assert.Contains("-Execute 'dotnet'", action, StringComparison.Ordinal);
        Assert.Contains("run --project src/AlphaLab.Worker", action, StringComparison.Ordinal);
        Assert.Contains("-WorkingDirectory", action, StringComparison.Ordinal);
        Assert.Contains("-Daily -At 02:00", trigger, StringComparison.Ordinal);
        Assert.Contains("-TaskName 'AlphaLab-sp500-NightlyRun'", register, StringComparison.Ordinal);

        // The load-bearing negative: --serve would start a resident Quartz host with ZERO registered
        // jobs, which idles forever and never takes a backup. The emitted task must be an OnDemand
        // launch, which drives the whole D72 order and exits.
        Assert.DoesNotContain("--serve", action, StringComparison.Ordinal);
        Assert.Contains("Nothing was registered", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FR25_RegisterNightly_RejectsAMalformedTime()
    {
        if (PowerShell() is not { } shell) return;

        using var fixture = new ScriptFixture();

        var (exit, output) = Run(shell, fixture.Script("register-nightly-backup.ps1"), "-Arena", "sp500", "-Time", "2am");

        Assert.True(exit != 0, "expected a non-zero exit on a malformed -Time. Output:\n" + output);
    }

    // ---- harness ----

    /// <summary>
    /// Strip ANSI escape sequences and collapse every whitespace run to a single space, so an
    /// assertion is about the MESSAGE rather than about the console the shell happened to render it
    /// on. PowerShell 7 colours error records and hard-wraps them at the terminal width; Windows
    /// PowerShell 5.1 does neither. Without this, a substring assertion silently depends on which
    /// shell the probe found — green locally on 5.1, red on a CI runner with pwsh.
    /// </summary>
    private static string Normalize(string output) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(output, @"\x1B\[[0-9;]*[a-zA-Z]", string.Empty),
            @"\s+", " ");

    /// <summary>pwsh (cross-platform) first, then Windows PowerShell. Null ⇒ skip: the suite must stay
    /// runnable on a machine with neither.</summary>
    private static string? PowerShell()
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "-NoProfile -Command \"exit 0\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (probe is null) continue;
                probe.WaitForExit(30_000);
                if (probe.ExitCode == 0) return candidate;
            }
            catch (Exception) { /* not installed — try the next */ }
        }
        return null;
    }

    private static (int ExitCode, string Output) Run(string shell, string scriptPath, params string[] args)
    {
        var info = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(scriptPath);
        foreach (var a in args) info.ArgumentList.Add(a);

        using var process = Process.Start(info)!;
        var output = new StringBuilder();
        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());
        process.WaitForExit(120_000);
        return (process.ExitCode, output.ToString());
    }

    private sealed class ScriptFixture : IDisposable
    {
        private readonly string _root;

        public string BackupDirectory { get; }
        public string Destination { get; }

        public ScriptFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "alphalab-scripts-" + Guid.NewGuid().ToString("N"));
            BackupDirectory = Path.Combine(_root, "backups");
            Destination = Path.Combine(_root, "offsite");
            Directory.CreateDirectory(BackupDirectory);
            Directory.CreateDirectory(Destination);
        }

        public string WriteBackup(string name, string content)
        {
            var path = Path.Combine(BackupDirectory, name);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>Resolve tools/&lt;name&gt; by walking up from the test binary to the repo root.</summary>
        public string Script(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tools")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            var path = Path.Combine(dir!.FullName, "tools", name);
            Assert.True(File.Exists(path), $"script not found: {path}");
            return path;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
