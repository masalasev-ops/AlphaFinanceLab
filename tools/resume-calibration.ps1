# Resume-safe launcher for the Phase-4.11 full-scale calibration run (RUNBOOK section 8).
#
# WHY THIS EXISTS. The full run is `replay-calibrate --reset --from ... --to ...`, and after a crash
# or a reboot the obvious recovery - press Up, press Enter - re-sends `--reset`, which calls
# ReplayRunner.DeleteReplayGeneration and destroys every committed replay day before starting over.
# Days are committed one at a time and already-committed days are skipped, so the run IS resumable;
# the only thing standing between you and a lost multi-day run is that one flag. This script NEVER
# emits --reset. To start a genuinely fresh generation, type the raw command deliberately.
#
# It also guards the two other ways a resume can silently corrupt the generation:
#   * the frozen watermark (D95) - re-resolved as MAX(observed_at) over bars + corporate_actions, so
#     any new bar ingested since the run started moves it and mixes replay vintages. Pass -Watermark
#     once; it is remembered next to the store and pinned automatically from then on.
#   * the machinery vintage - `dotnet run` rebuilds, so a resume against edited src/ would continue
#     one generation with different code. A dirty src/ working tree stops the launch.
#
# Build configuration is pinned to Release to match the generation's report stamp (finding 278).

[CmdletBinding()]
param(
    [string]$Arena = 'sp500',
    [string]$From = '2006-01-01',
    [string]$To = '2026-01-01',
    [string]$LearnThrough = '2019-12-31',
    [string]$Watermark,
    [switch]$ReportOnly,
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-AlphaLabConnection.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$cs = Resolve-AlphaLabConnectionString -Arena $Arena
$dbPath = Get-AlphaLabDataSourcePath -ConnectionString $cs
$pinFile = Join-Path (Split-Path -Parent $dbPath) 'calibration-generation.txt'

# ---- guard 1: a Worker is already running -------------------------------------------------------
# The DB-level writer guard would catch this, but only after a rebuild and a startup - and the error
# it raises reads like a stale-flag problem rather than "you already have one of these running".
$running = @(Get-Process -Name 'AlphaLab.Worker' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $ids = ($running | ForEach-Object { $_.Id }) -join ', '
    throw "An AlphaLab.Worker is ALREADY running (PID $ids). The calibration is probably still going - check tools/replay-progress.ps1 before launching a second writer."
}

# ---- guard 2: the machinery vintage -------------------------------------------------------------
# One generation must be one code vintage. `dotnet run` rebuilds from source, so resuming against
# edited src/ would interleave days produced by different machinery into the same watermark - which
# the GuardSingleGeneration watermark check cannot see and the D56 curves would silently absorb.
$dirty = @()
try {
    Push-Location $repoRoot
    $dirty = @(& git status --porcelain -- src/ 2>$null | Where-Object { $_ })
}
finally { Pop-Location }

if ($dirty.Count -gt 0 -and -not $Force) {
    $sample = ($dirty | Select-Object -First 5) -join "`n  "
    throw @"
src/ has uncommitted changes - refusing to resume the calibration against edited machinery:
  $sample
Resuming would continue ONE replay generation with DIFFERENT code, which no watermark check can
detect. Either stash/commit and rebuild deliberately (then the generation needs a --reset re-run),
or pass -Force if you are certain these changes cannot reach the replay path.
"@
}

# ---- the frozen watermark pin -------------------------------------------------------------------
if ($Watermark) {
    Set-Content -Path $pinFile -Value $Watermark -Encoding ascii
    Write-Host "Pinned generation watermark $Watermark (remembered in $pinFile)."
}
elseif (Test-Path $pinFile) {
    $Watermark = (Get-Content -Path $pinFile -TotalCount 1).Trim()
    Write-Host "Using remembered generation watermark $Watermark (from $pinFile)."
}
else {
    Write-Warning @"
No -Watermark given and no pin recorded at $pinFile.
The Worker will re-resolve MAX(observed_at) itself. If ANY bar or corporate action has been ingested
since this generation started, it will refuse to continue and print the generation's actual
watermark - re-run this script with -Watermark <that value> and it will be remembered.
"@
}

# ---- launch --------------------------------------------------------------------------------------
# NOTE: no --reset, by construction. See the header.
$workerArgs = @(
    'run', '--project', 'src/AlphaLab.Worker', '-c', 'Release', '--',
    'replay-calibrate',
    '--arena', $Arena,
    '--from', $From,
    '--to', $To,
    '--learn-through', $LearnThrough
)
if ($Watermark) { $workerArgs += @('--watermark', $Watermark) }
if ($ReportOnly) { $workerArgs += '--report-only' }

Write-Host ''
Write-Host "  arena   : $Arena"
Write-Host "  store   : $dbPath"
Write-Host "  window  : $From .. $To   (learn through $LearnThrough)"
if ($ReportOnly) { Write-Host '  mode    : --report-only (curves + report re-derived; NO config freeze)' }
else { Write-Host '  mode    : full chain (replay -> curves -> report -> config freeze)' }
Write-Host "  command : dotnet $($workerArgs -join ' ')"
Write-Host ''

if ($DryRun) {
    Write-Host 'DryRun: nothing launched.'
    return
}

Push-Location $repoRoot
try {
    & dotnet @workerArgs
    $code = $LASTEXITCODE
}
finally { Pop-Location }

if ($code -ne 0) {
    Write-Warning "replay-calibrate exited $code. Committed days PERSIST - re-run this script to resume from where it stopped."
}
exit $code
