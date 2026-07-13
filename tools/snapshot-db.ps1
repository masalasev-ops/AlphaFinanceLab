# Pre-migration snapshot of the arena's SQLite store (rule 14). Windows/PowerShell first-class.
# Copies the db file (and any -wal/-shm sidecars) into an arena-namespaced snapshots dir next to it.
param([string]$Arena = 'sp500')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-AlphaLabConnection.ps1')

$cs = Resolve-AlphaLabConnectionString -Arena $Arena
$dbPath = Get-AlphaLabDataSourcePath -ConnectionString $cs

if (-not (Test-Path $dbPath)) {
    Write-Host "No database at '$dbPath' yet - nothing to snapshot (fresh install)."
    return
}

$snapshotDir = Join-Path (Split-Path -Parent $dbPath) 'snapshots'
New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$baseName = [System.IO.Path]::GetFileNameWithoutExtension($dbPath)

foreach ($suffix in @('', '-wal', '-shm')) {
    $src = "$dbPath$suffix"
    if (Test-Path $src) {
        $dest = Join-Path $snapshotDir "$baseName-$stamp.db$suffix"
        Copy-Item -Path $src -Destination $dest -Force
        Write-Host "Snapshot: $dest"
    }
}
