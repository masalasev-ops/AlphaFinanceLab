# Emit (and, only with -Install, register) a Windows Task Scheduler job that launches the Worker
# nightly, so a backup lands even on a day the operator never opens the lab (RUNBOOK section 3, FR-25).
#
# WHY OnDemand AND NOT --serve. The obvious reading of "a scheduled nightly backup" is a resident
# Worker in Scheduled mode (D61). That does NOT work today: Scheduled mode registers Quartz with ZERO
# jobs (Program.cs), so a resident Worker would start, heartbeat, and idle forever - never catching
# up, never backing up. The OnDemand launch is the one that is fully built: it drives the D72 launch
# order (stale-run recovery -> catch-up -> job drain -> LocalBackup -> exit) and terminates. Running
# it at 02:00 gives exactly the property that was wanted, with no code change. The missing Quartz
# daily job is tracked in PROGRESS as a Phase-2/D61 leftover.
#
# Safe to run nightly: LocalBackup is idempotent per calendar day (today's copy exists -> prune only),
# and catch-up is a no-op once current.
#
# PRINTS BY DEFAULT, INSTALLS ONLY ON REQUEST. The operator reads the exact command before anything
# is registered on their machine - this script chooses no destinations and installs nothing silently.
#
# ASCII-only (BUILD 0.6): PS 5.1 mis-decodes a UTF-8 em dash in a BOM-less script, so use - and --.
#
#   pwsh tools/register-nightly-backup.ps1 -Arena sp500
#   pwsh tools/register-nightly-backup.ps1 -Arena sp500 -Time 02:00 -Install

param(
    [string]$Arena = 'sp500',
    [string]$Time = '02:00',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

if ($Time -notmatch '^([01][0-9]|2[0-3]):[0-5][0-9]$') {
    throw "-Time must be HH:mm on a 24-hour clock (got '$Time')."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$workerProject = Join-Path $repoRoot 'src/AlphaLab.Worker'
if (-not (Test-Path -LiteralPath $workerProject)) {
    throw "Worker project not found at '$workerProject'."
}

$taskName = "AlphaLab-$Arena-NightlyRun"
$workerArgs = 'run --project src/AlphaLab.Worker'   # NOTE the deliberate absence of --serve (see header)

# Register-ScheduledTask, NOT schtasks.exe /TR. The repo path contains spaces, and schtasks packs the
# whole command line into ONE quoted /TR argument - so a working-directory hop has to be spelled
# 'cmd /c cd /d "<path>" && dotnet run ...', whose inner quotes terminate the outer /TR string and
# silently mangle the task. Register-ScheduledTask takes -WorkingDirectory as its own parameter, so
# the path never has to survive a second round of quoting.
$registerCommand = @"
`$action  = New-ScheduledTaskAction -Execute 'dotnet' -Argument '$workerArgs' -WorkingDirectory '$repoRoot'
`$trigger = New-ScheduledTaskTrigger -Daily -At $Time
Register-ScheduledTask -TaskName '$taskName' -Action `$action -Trigger `$trigger -Description 'AlphaLab $Arena nightly OnDemand Worker launch (catch up, drain jobs, backup, exit).' -Force
"@

Write-Host "Nightly Worker launch for arena '$Arena' (OnDemand: catch up -> drain jobs -> backup -> exit)."
Write-Host ""
Write-Host "  task name: $taskName"
Write-Host "  schedule:  daily at $Time"
Write-Host "  runs:      dotnet $workerArgs   (OnDemand launch, deliberately not the resident mode)"
Write-Host "  workdir:   $repoRoot"
Write-Host ""

# Build the task objects for real when the ScheduledTasks module is present (Windows). This is not
# ceremony: constructing them is what proves the emitted definition is well formed rather than a
# plausible-looking string, and it registers nothing.
$scheduledTasksAvailable = [bool](Get-Command New-ScheduledTaskAction -ErrorAction SilentlyContinue)
if ($scheduledTasksAvailable) {
    $action = New-ScheduledTaskAction -Execute 'dotnet' -Argument $workerArgs -WorkingDirectory $repoRoot
    $trigger = New-ScheduledTaskTrigger -Daily -At $Time
    Write-Host "Task definition validated (action + daily trigger constructed)." -ForegroundColor Green
}
else {
    Write-Host "NOTE: the ScheduledTasks module is not available here (non-Windows), so the definition was printed but not constructed."
}

Write-Host ""
Write-Host "Review these commands, then run them (or re-run this script with -Install):" -ForegroundColor Cyan
Write-Host ""
Write-Host $registerCommand
Write-Host ""

if (-not $Install) {
    Write-Host "Nothing was registered (-Install not supplied)."
    Write-Host "To remove it later: Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false"
    return
}

if (-not $scheduledTasksAvailable) {
    throw "-Install needs the Windows ScheduledTasks module, which is not available on this host."
}

Write-Host "Registering the scheduled task..." -ForegroundColor Yellow
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Description "AlphaLab $Arena nightly OnDemand Worker launch (catch up, drain jobs, backup, exit)." -Force | Out-Null

Write-Host "Registered '$taskName' (daily at $Time)." -ForegroundColor Green
Write-Host "To remove it later: Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false"
