# Copy the arena's LATEST local backup to an off-machine destination, and verify the copy (RUNBOOK
# section 3, FR-25). The per-launch LocalBackup writes a dated copy next to the store on the SAME
# DRIVE - a convenience snapshot, not a safeguard. This script is the off-machine leg: the operator
# runs it weekly against a UNC share, an external disk, or a mounted cloud folder.
#
# It NEVER touches the database: it copies an already-checkpointed backup file. Nothing here opens
# the store, so it can run while the Worker is running (D59 sole writer is unaffected).
#
# Fails LOUDLY, never silently (rule 10): a missing destination, a missing backup directory, no
# backup files, or a hash mismatch all throw. A backup routine that quietly does nothing is worse
# than no backup routine, because it also removes the operator's reason to check.
#
# ASCII-only (BUILD 0.6): Windows PowerShell 5.1 reads a BOM-less script as ANSI and mis-decodes a
# UTF-8 em dash, so use - and -- only.
#
#   pwsh tools/backup-offsite.ps1 -Arena sp500 -Destination \\nas\alphalab
#   pwsh tools/backup-offsite.ps1 -Arena sp500 -Destination D:\offsite -BackupDirectory C:\some\backups

param(
    [string]$Arena = 'sp500',
    [Parameter(Mandatory)][string]$Destination,
    # Escape hatch for a relocated or non-standard layout, and what the tests drive (the migrate.ps1
    # --connection precedent). Default: the arena's own backups dir, exactly where LocalBackup writes.
    [string]$BackupDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-AlphaLabConnection.ps1')

if ([string]::IsNullOrWhiteSpace($Destination)) {
    throw "-Destination is required and must not be blank. Refusing to 'succeed' without copying anything."
}

# 1. Locate the source directory (the same one DbPathResolver.BackupDirectory returns).
if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
    $cs = Resolve-AlphaLabConnectionString -Arena $Arena
    $dbPath = Get-AlphaLabDataSourcePath -ConnectionString $cs
    $BackupDirectory = Join-Path (Split-Path -Parent $dbPath) 'backups'
}

if (-not (Test-Path -LiteralPath $BackupDirectory)) {
    throw "No backup directory at '$BackupDirectory'. Launch the Worker at least once (its final step takes the per-launch backup), then re-run."
}

# 2. Pick the newest backup BY THE DATE IN THE FILENAME, mirroring LocalBackup.TryParseBackupDate.
#    Deliberately not by LastWriteTime: a file copy or a restore drill rewrites mtimes, and the
#    filename date is what LocalBackup's own retention prune keys off. One rule, both places.
$pattern = '^alphalab-(\d{4}-\d{2}-\d{2})\.db$'
$candidates = Get-ChildItem -LiteralPath $BackupDirectory -Filter 'alphalab-*.db' -File |
    Where-Object { $_.Name -match $pattern } |
    Sort-Object { [datetime]::ParseExact(([regex]::Match($_.Name, $pattern)).Groups[1].Value, 'yyyy-MM-dd', $null) }

if (-not $candidates) {
    throw "No 'alphalab-<yyyy-MM-dd>.db' backup found in '$BackupDirectory'. Nothing to copy off-machine."
}
$source = $candidates[-1]

# 3. Ensure the destination exists (a typo'd UNC path must fail here, not silently create a local dir
#    named like the share - New-Item on an unreachable UNC throws, which is what we want).
if (-not (Test-Path -LiteralPath $Destination)) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
}
$target = Join-Path $Destination $source.Name

# 4. Copy, then VERIFY. An unverified off-site copy is the same hope-not-a-plan the restore drill
#    exists to replace: a truncated network copy looks exactly like a successful one until you need it.
Write-Host "Copying $($source.FullName) -> $target"
Copy-Item -LiteralPath $source.FullName -Destination $target -Force

$sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
$targetItem = Get-Item -LiteralPath $target
$targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash

if ($source.Length -ne $targetItem.Length) {
    throw "VERIFY FAILED: size mismatch (source $($source.Length) bytes, copy $($targetItem.Length) bytes) for '$target'."
}
if ($sourceHash -ne $targetHash) {
    throw "VERIFY FAILED: SHA-256 mismatch for '$target' (source $sourceHash, copy $targetHash)."
}

Write-Host "Off-site backup VERIFIED." -ForegroundColor Green
Write-Host "  arena:       $Arena"
Write-Host "  source:      $($source.FullName)"
Write-Host "  destination: $target"
Write-Host "  size:        $($source.Length) bytes"
Write-Host "  sha256:      $sourceHash"
Write-Host "Log this copy in PROGRESS.md (RUNBOOK section 3: monthly off-site log)."
