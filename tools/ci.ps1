# Local CI: build, test, and guard greps. Mirrors what a CI server would run.
#   tools/ci.ps1                 build + test + guards
#   tools/ci.ps1 -SkipTests      build + guards only
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Native {
    param([Parameter(Mandatory)][scriptblock]$Command, [Parameter(Mandatory)][string]$What)
    # Run a native command tolerant of stderr warnings (NU1903), gate on the exit code.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $Command
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previousEap
    if ($code -ne 0) { throw "$What failed with exit code $code." }
}

function Assert-NoMatch {
    param(
        [Parameter(Mandatory)][string[]]$Files,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Files) { return }
    $hits = Select-String -Path $Files -Pattern $Pattern -AllMatches -ErrorAction SilentlyContinue
    if ($hits) {
        Write-Host "GUARD FAILED: $Message" -ForegroundColor Red
        $hits | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber): $($_.Line.Trim())" -ForegroundColor Red }
        throw "Guard grep failed: $Message"
    }
}

function Get-CommittableFiles {
    # Files eligible to be committed: tracked + untracked-but-not-ignored (so gitignored
    # appsettings.Secrets.json is excluded). Falls back to a working-tree scan if git is absent.
    Push-Location $repoRoot
    try {
        # Relax EAP around the native git call: under $ErrorActionPreference='Stop', a missing git
        # executable throws before $LASTEXITCODE can be read (defeating the fallback), and native
        # stderr can raise a terminating NativeCommandError (PS 5.1). Same guard the repo uses in
        # Invoke-Native / migrate.ps1 (finding 119 class). Gate on presence + exit code, not stderr.
        $files = $null
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            if (Get-Command git -ErrorAction SilentlyContinue) {
                $files = git ls-files --cached --others --exclude-standard 2>$null
                if ($LASTEXITCODE -ne 0) { $files = $null }
            }
        }
        catch { $files = $null }
        finally { $ErrorActionPreference = $previousEap }

        if (-not $files) {
            $files = Get-ChildItem -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
                ForEach-Object { Resolve-Path -Relative $_.FullName }
        }
        return $files | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ }
    }
    finally { Pop-Location }
}

function Assert-ReferenceGraph {
    # Full reference-graph guard (D57 — CI-enforced at the <ProjectReference> level, matching BUILD
    # 0.1). Each src project may reference ONLY the AlphaLab.* projects in its allowlist; an illegal
    # edge (e.g. Web -> Data, or Evaluation -> Strategies) fails CI. This makes D57's swappable-UI
    # promise and D58's honesty placement structural, not aspirational.
    $allowed = @{
        'AlphaLab.Core'       = @()
        'AlphaLab.Data'       = @('AlphaLab.Core')
        'AlphaLab.Strategies' = @('AlphaLab.Core', 'AlphaLab.Data')
        'AlphaLab.Llm'        = @('AlphaLab.Core')
        'AlphaLab.Evaluation' = @('AlphaLab.Core', 'AlphaLab.Data')
        'AlphaLab.Api'        = @('AlphaLab.Core', 'AlphaLab.Data', 'AlphaLab.Evaluation')
        'AlphaLab.Worker'     = @('AlphaLab.Core', 'AlphaLab.Data', 'AlphaLab.Evaluation', 'AlphaLab.Strategies', 'AlphaLab.Llm')
        'AlphaLab.Web'        = @('AlphaLab.Core')
    }
    $violations = @()
    foreach ($proj in $allowed.Keys) {
        $csproj = Join-Path $repoRoot "src/$proj/$proj.csproj"
        if (-not (Test-Path $csproj)) { $violations += "${proj}: csproj not found at src/$proj/"; continue }
        $refs = Select-String -Path $csproj -Pattern '<ProjectReference[^>]*Include="[^"]*[\\/](AlphaLab\.[A-Za-z]+)\.csproj"' -AllMatches |
            ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        foreach ($ref in $refs) {
            if ($allowed[$proj] -notcontains $ref) {
                $violations += "$proj must not reference $ref (allowed: $($allowed[$proj] -join ', '))."
            }
        }
    }
    if ($violations) {
        Write-Host 'GUARD FAILED: reference graph (D57)' -ForegroundColor Red
        $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw 'Guard failed: reference-graph violation (D57).'
    }
}

Push-Location $repoRoot
try {
    Write-Host '== build ==' -ForegroundColor Cyan
    Invoke-Native -What 'dotnet build' -Command { dotnet build 'AlphaLab.slnx' -c Debug --nologo }

    # Report-only vulnerability audit (after build so restore assets exist; runs under -SkipTests too).
    # NOT a gate yet: two transitive NU1903 advisories (SQLitePCLRaw.lib.e_sqlite3 2.1.11 via EF Core;
    # Microsoft.OpenApi 2.0.0 via AspNetCore.OpenApi) are non-blocking today and clear on Microsoft's next
    # 10.0.x servicing bump. Make this a HARD gate (throw on any advisory) once they clear. EAP is relaxed
    # so a native stderr line / non-zero exit here never fails CI (report only).
    Write-Host '== vuln audit (report-only) ==' -ForegroundColor Cyan
    $vulnEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    dotnet list 'AlphaLab.slnx' package --vulnerable --include-transitive
    $ErrorActionPreference = $vulnEap

    if (-not $SkipTests) {
        Write-Host '== test ==' -ForegroundColor Cyan
        # Category!=LiveSmoke excludes the ONE test that calls a real vendor endpoint (TEST_PLAN §6).
        # A trait filter, not an env flag: D67 bars environment variables for configuration, and the same
        # reasoning applies to gating — the exclusion belongs here, where it is visible and reproducible,
        # rather than in a machine's environment where it is neither. Run it deliberately with
        #   dotnet test tests/AlphaLab.Worker.Tests --filter "Category=LiveSmoke"
        Invoke-Native -What 'dotnet test' -Command {
            dotnet test 'AlphaLab.slnx' -c Debug --nologo --no-build --filter 'Category!=LiveSmoke'
        }
    }

    Write-Host '== guards ==' -ForegroundColor Cyan

    # 1. Bars are versioned append-only - never UPDATE or DELETE a bar row (rule 3). Word-boundary
    #    \bbars\b so 'UPDATE barstool' does not false-positive (v1.9.6).
    $codeFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tests'), (Join-Path $repoRoot 'tools') -Recurse -File -Include *.cs, *.sql -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $codeFiles -Pattern 'DELETE\s+FROM\s+bars\b' -Message 'DELETE FROM bars is forbidden (rule 3).'
    Assert-NoMatch -Files $codeFiles -Pattern 'UPDATE\s+bars\b'         -Message 'UPDATE bars is forbidden (rule 3).'

    # 1b. corporate_actions is versioned append-only too (D76 extended rule 3 to a second table); the
    #     guard follows. A restatement INSERTs a new version — never an UPDATE/DELETE. (The always-NULL
    #     processed_on column was dropped by D94/M5, resolving P5.)
    Assert-NoMatch -Files $codeFiles -Pattern 'DELETE\s+FROM\s+corporate_actions\b' -Message 'DELETE FROM corporate_actions is forbidden (D76 append-only).'
    Assert-NoMatch -Files $codeFiles -Pattern 'UPDATE\s+corporate_actions\b'         -Message 'UPDATE corporate_actions is forbidden (D76 append-only).'

    # 1c. config is the THIRD append-only-versioned table (rule 24 / D72, finding 108): a change INSERTs
    #     (key, version+1) and the current value is MAX(version) per key. Never an UPDATE or DELETE - the
    #     frozen calibration rows ARE the audit trail, and a mutated one is indistinguishable from a value
    #     that was always there. Added at v1.9.85 (finding 365): the invariant held by review alone until
    #     now, and this corpus has already shown (finding 363) that an unchecked invariant can be broken
    #     for two generations without anyone noticing.
    #
    #     SCOPED TO src + tools, NOT tests - deliberately, and this is the one exclusion in the guard set.
    #     DetectabilityGateTests re-seeds a fixture DB by clearing its Calibration.DetectionPower row to
    #     exercise the no-curves branch; that is a test constructing a scenario, not the lab mutating its
    #     own config history. If a future test needs the same, prefer a fresh arena.
    $srcToolFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tools') -Recurse -File -Include *.cs, *.sql -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $srcToolFiles -Pattern 'DELETE\s+FROM\s+config\b' -Message 'DELETE FROM config is forbidden - config is append-only-versioned (rule 24 / D72).'
    Assert-NoMatch -Files $srcToolFiles -Pattern 'UPDATE\s+config\b'         -Message 'UPDATE config is forbidden - a change INSERTs (key, version+1) (rule 24 / D72).'
    Assert-NoMatch -Files $srcToolFiles -Pattern 'Config\.Remove(Range)?\s*\(' -Message 'db.Config.Remove/RemoveRange is forbidden - config is append-only-versioned (rule 24 / D72).'

    # 2. No committed secret-key material (D67). appsettings.Secrets.json is gitignored, so it is
    #    excluded from the committable set below.
    $committable = Get-CommittableFiles
    Assert-NoMatch -Files $committable -Pattern 'sk-ant-[A-Za-z0-9]{12,}' -Message 'An Anthropic API key pattern is present in a committable file.'
    if ($committable | Where-Object { $_ -match 'appsettings\.Secrets\.json$' }) {
        throw 'Guard failed: appsettings.Secrets.json is committable - it must be gitignored (D67).'
    }

    # 3. The full reference graph is CI-enforced at the <ProjectReference> level (D57, BUILD 0.1):
    #    every src project may reference only the AlphaLab.* projects in its allowlist.
    Assert-ReferenceGraph

    # Register integrity (rule 25 / D109): a register row is changed only by another register row.
    # Baseline mode - the violations that predate the check are carried; anything new fails.
    & (Join-Path $PSScriptRoot 'check-register.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'check-register failed.' }

    # 3b. Belt-and-suspenders at the SOURCE level for the UI boundary: AlphaLab.Web must not even
    #     `using` Evaluation/Data (a source reach the graph check cannot see, e.g. a transitive type).
    $webDir = Join-Path $repoRoot 'src/AlphaLab.Web'
    $webCs = Get-ChildItem -Path $webDir -Recurse -File -Include *.cs, *.razor -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $webCs -Pattern 'using\s+AlphaLab\.(Evaluation|Data)' -Message 'AlphaLab.Web must not use AlphaLab.Evaluation/AlphaLab.Data (D57).'

    # 4. The Signal Library is DESCRIPTIVE ONLY (D91, MASTER 24.5): its output is never an input to the
    #    allocator, any gate, sizing, or eligibility. Scoped to those CONSUMER directories on purpose -
    #    an unscoped scan would fire on the library's own engine, read-model, entities and tests.
    #
    #    This is the belt; the brace is DescriptiveOnlyGuardTests, an assembly-scoped reflection closure
    #    that catches a DI-injected consumer this text scan cannot see AND runs on both CI legs (greps
    #    run on the Windows leg only). Keep both: the grep catches a raw table read in SQL that carries
    #    no type, the closure catches a typed dependency that mentions no token.
    $consumerDirs = @(
        'src/AlphaLab.Evaluation/Allocator', 'src/AlphaLab.Evaluation/Gate',
        'src/AlphaLab.Evaluation/Candidates', 'src/AlphaLab.Evaluation/Power',
        'src/AlphaLab.Core/Funnel'
    ) | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ }
    $consumerCs = @()
    if ($consumerDirs) {
        $consumerCs = Get-ChildItem -Path $consumerDirs -Recurse -File -Include *.cs -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    }
    Assert-NoMatch -Files $consumerCs -Pattern 'signal_ic|ISignal\b|SignalIc|SignalLibrary' -Message 'The Signal Library is descriptive only (D91) - the allocator/gate/sizing/eligibility must never read it.'

    # 5. Ledger money is C# decimal persisted as TEXT, NEVER double/REAL (rule 20 / D69). Added at
    #    v1.9.85 (finding 365) for the same reason as 1c: the invariant was held by review alone.
    #
    #    The pattern is a NAME LIST, not a type scan, and that is the whole design of it. Money and rates
    #    are both numbers; only the name distinguishes them. `double ImpactK`, `double HalfSpreadBp` and
    #    `double ParticipationCapPctAdv` are CORRECT - a coefficient, a basis-point rate and a percentage
    #    are not money and must not be forced to decimal. So the guard names the members that carry
    #    currency and says nothing about anything else. It is a RATCHET: it catches a new money member
    #    declared as double, which is how this defect would actually arrive.
    #
    #    TWO SCOPING DECISIONS, both learned by RUNNING the guard rather than reasoning about it - its
    #    first execution failed on two false positives, which is the argument for writing it at all:
    #
    #    (a) ANCHORED ON AN ACCESS MODIFIER, so it matches a DECLARATION and not prose. Without this it
    #        fired on `// ... A double commission would ...` - a comment explaining this very rule.
    #    (b) src ONLY, never tests. A test theory cannot comply even in principle: C# attribute arguments
    #        may not be decimal constants, so `[InlineData]` money MUST be double or string and a test
    #        signature like `(double cash)` is forced, not sloppy. A guard that cannot be satisfied is a
    #        guard that gets deleted.
    #
    #    KNOWN GAP, stated rather than hidden: a record POSITIONAL parameter (`record X(double Cash)`)
    #    carries no access modifier and so is not matched. Every money member in src today is a property
    #    with a modifier; if that changes, widen this.
    $moneyNames = 'StartingCash|CostBasis|RawFillPrice|Commission|SpreadCost|ImpactCost|CashDelta|' +
                  'TotalCost|CashPerShare|LastPrintPrice|SpinoffBasisAllocated|Equity|Cash|Amount'
    $srcCs = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -File -Include *.cs -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $srcCs -Pattern ('\b(public|private|internal|protected)\s+(double|float)\??\s+(' + $moneyNames + ')\b') `
        -Message 'Ledger money must be decimal, never double/float (rule 20 / D69).'

    Write-Host 'CI OK' -ForegroundColor Green
}
finally {
    Pop-Location
}
