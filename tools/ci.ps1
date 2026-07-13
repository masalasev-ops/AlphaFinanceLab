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
        $files = git ls-files --cached --others --exclude-standard 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $files) {
            $files = Get-ChildItem -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
                ForEach-Object { Resolve-Path -Relative $_.FullName }
        }
        return $files | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ }
    }
    finally { Pop-Location }
}

Push-Location $repoRoot
try {
    Write-Host '== build ==' -ForegroundColor Cyan
    Invoke-Native -What 'dotnet build' -Command { dotnet build 'AlphaLab.slnx' -c Debug --nologo }

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

    # 2. No committed secret-key material (D67). appsettings.Secrets.json is gitignored, so it is
    #    excluded from the committable set below.
    $committable = Get-CommittableFiles
    Assert-NoMatch -Files $committable -Pattern 'sk-ant-[A-Za-z0-9]{12,}' -Message 'An Anthropic API key pattern is present in a committable file.'
    if ($committable | Where-Object { $_ -match 'appsettings\.Secrets\.json$' }) {
        throw 'Guard failed: appsettings.Secrets.json is committable - it must be gitignored (D67).'
    }

    # 3. AlphaLab.Web references AlphaLab.Core ONLY (D57) - never Evaluation/Data, at source or project level.
    $webDir = Join-Path $repoRoot 'src/AlphaLab.Web'
    $webCs = Get-ChildItem -Path $webDir -Recurse -File -Include *.cs, *.razor -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $webCs -Pattern 'using\s+AlphaLab\.(Evaluation|Data)' -Message 'AlphaLab.Web must not use AlphaLab.Evaluation/AlphaLab.Data (D57).'

    $webProj = Get-ChildItem -Path $webDir -Recurse -File -Include *.csproj -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $webProj -Pattern '<ProjectReference[^>]*AlphaLab\.(Evaluation|Data)\.csproj' -Message 'AlphaLab.Web must not ProjectReference AlphaLab.Evaluation/AlphaLab.Data (D57).'

    Write-Host 'CI OK' -ForegroundColor Green
}
finally {
    Pop-Location
}
