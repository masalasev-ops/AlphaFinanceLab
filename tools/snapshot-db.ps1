# Pre-migration snapshot of the arena's SQLite store (rule 14). Windows/PowerShell first-class.
# Copies the db file (and any -wal/-shm sidecars) into an arena-namespaced snapshots dir next to it.
param([string]$Arena = 'sp500')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-AlphaLabConnection.ps1')

$cs = Resolve-AlphaLabConnectionString -Arena $Arena
$dbPath = Get-AlphaLabDataSourcePath -ConnectionString $cs

# Fail-CLOSED existence check (finding 265): the old bare Test-Path read a transient antivirus/indexer
# access-denied as "no database - fresh install" and SKIPPED the snapshot, which let migrate.ps1
# proceed to migrate the live store snapshotless. Absence is now believed only when the parent
# directory enumerates and the file is not in it; an indeterminate answer throws.
if (-not (Test-AlphaLabStoreExists -DbPath $dbPath)) {
    Write-Host "No database at '$dbPath' yet - nothing to snapshot (fresh install)."
    return
}

$snapshotDir = Join-Path (Split-Path -Parent $dbPath) 'snapshots'
New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$baseName = [System.IO.Path]::GetFileNameWithoutExtension($dbPath)

$copied = 0
foreach ($suffix in @('', '-wal', '-shm')) {
    $src = "$dbPath$suffix"
    if (Test-Path $src) {
        $dest = Join-Path $snapshotDir "$baseName-$stamp.db$suffix"
        Copy-Item -Path $src -Destination $dest -Force
        # Verify the copy LANDED and is whole (the backup-offsite discipline): a snapshot that
        # silently failed is worse than none - it also removes your reason to check.
        $srcLen = (Get-Item -Path $src).Length
        $destLen = (Get-Item -Path $dest).Length
        if ($destLen -ne $srcLen) {
            throw "Snapshot verification FAILED: '$dest' is $destLen bytes but the source is $srcLen. Do not migrate."
        }
        Write-Host "Snapshot: $dest ($destLen bytes, size-verified)"
        $copied++
    }
}

if ($copied -eq 0) {
    # The store exists (proven above) but nothing was copied - the main file vanished between the
    # existence check and the copy loop, or every Test-Path in the loop hit the same transient lock.
    throw "Snapshot produced NO files although the store exists at '$dbPath' - refusing to continue (rule 14). Retry."
}
