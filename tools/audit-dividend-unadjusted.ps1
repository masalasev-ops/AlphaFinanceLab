# audit-dividend-unadjusted.ps1  (P1R-1, v1.9.10)
#
# READ-ONLY audit. Re-parses the archived EODHD /div payloads and reports how many
# dividend events carry a null / absent "unadjustedValue" (the actual cash a holder
# received). Those rows are the ones the old CorporateActionIngestion fallback would
# have written as the split-adjusted value instead. This script changes NO data.
#
# Why a tools/ script and not a test: the raw cache is gitignored and machine-specific,
# so a CI test reading it would be non-portable (it would fail on any clone without a
# live cache). This is a one-shot operator audit.
#
#   tools/audit-dividend-unadjusted.ps1                 # default cache root
#   tools/audit-dividend-unadjusted.ps1 -CacheRoot X    # a copied-off cache
param([string]$CacheRoot)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $CacheRoot) { $CacheRoot = Join-Path $repoRoot 'tools/raw-cache/eodhd' }

if (-not (Test-Path $CacheRoot)) {
    Write-Host "No cache directory at $CacheRoot - nothing to audit (payloads may not be present on this machine)." -ForegroundColor Yellow
    Write-Host "A re-fetch to reconstruct it would cost 1 EODHD /div call per symbol (INTEGRATIONS 1)."
    return
}

$files = @(Get-ChildItem -Path $CacheRoot -Recurse -File -Filter '*.div.json')
$totalEvents = 0
$nullEvents = 0
$affectedFiles = 0

foreach ($f in $files) {
    $raw = Get-Content -Raw -LiteralPath $f.FullName
    if ([string]::IsNullOrWhiteSpace($raw)) { continue }
    # Assign first, THEN normalize: `@($raw | ConvertFrom-Json)` wraps the whole array as a
    # single element in Windows PowerShell 5.1 (ConvertFrom-Json emits the array as one object).
    $parsed = $raw | ConvertFrom-Json
    if ($null -eq $parsed) { continue }
    $events = @($parsed)   # single-object payload -> 1-elem array; a real array stays intact
    $fileNull = 0
    foreach ($e in $events) {
        if ($null -eq $e) { continue }
        $totalEvents++
        $hasProp = ($e.PSObject.Properties.Name -contains 'unadjustedValue')
        if (-not $hasProp -or $null -eq $e.unadjustedValue) {
            $nullEvents++
            $fileNull++
        }
    }
    if ($fileNull -gt 0) {
        $affectedFiles++
        Write-Host ("  {0}: {1} null / {2} events" -f $f.Name, $fileNull, $events.Count) -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host ("Audited {0} .div.json payloads under {1}" -f $files.Count, $CacheRoot)
Write-Host ("Total dividend events:                     {0}" -f $totalEvents)
Write-Host ("Events with null/absent unadjustedValue:   {0}  (in {1} files)" -f $nullEvents, $affectedFiles) -ForegroundColor Cyan
if ($nullEvents -eq 0) {
    Write-Host "PASS: every archived dividend carries an unadjustedValue - the fail-closed throw is safe." -ForegroundColor Green
} else {
    Write-Host "STOP: some dividends lack unadjustedValue - a hard parse throw would brick the next backfill. Report before shipping the throw." -ForegroundColor Red
}
