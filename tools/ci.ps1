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
        Invoke-Native -What 'dotnet test' -Command { dotnet test 'AlphaLab.slnx' -c Debug --nologo --no-build }
    }

    Write-Host '== guards ==' -ForegroundColor Cyan

    # 1. Bars are versioned append-only - never UPDATE or DELETE a bar row (rule 3). Word-boundary
    #    \bbars\b so 'UPDATE barstool' does not false-positive (v1.9.6).
    $codeFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tests'), (Join-Path $repoRoot 'tools') -Recurse -File -Include *.cs, *.sql -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $codeFiles -Pattern 'DELETE\s+FROM\s+bars\b' -Message 'DELETE FROM bars is forbidden (rule 3).'
    Assert-NoMatch -Files $codeFiles -Pattern 'UPDATE\s+bars\b'         -Message 'UPDATE bars is forbidden (rule 3).'

    # 1b. corporate_actions is versioned append-only too (D76 extended rule 3 to a second table); the
    #     guard follows. A restatement INSERTs a new version — never an UPDATE/DELETE — and processed_on
    #     is never written (a global column on a per-account op would break replay; SCHEMA note + P5).
    Assert-NoMatch -Files $codeFiles -Pattern 'DELETE\s+FROM\s+corporate_actions\b' -Message 'DELETE FROM corporate_actions is forbidden (D76 append-only).'
    Assert-NoMatch -Files $codeFiles -Pattern 'UPDATE\s+corporate_actions\b'         -Message 'UPDATE corporate_actions is forbidden (D76 append-only; processed_on is never written).'

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

    # 3b. Belt-and-suspenders at the SOURCE level for the UI boundary: AlphaLab.Web must not even
    #     `using` Evaluation/Data (a source reach the graph check cannot see, e.g. a transitive type).
    $webDir = Join-Path $repoRoot 'src/AlphaLab.Web'
    $webCs = Get-ChildItem -Path $webDir -Recurse -File -Include *.cs, *.razor -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $webCs -Pattern 'using\s+AlphaLab\.(Evaluation|Data)' -Message 'AlphaLab.Web must not use AlphaLab.Evaluation/AlphaLab.Data (D57).'

    Write-Host 'CI OK' -ForegroundColor Green
}
finally {
    Pop-Location
}
