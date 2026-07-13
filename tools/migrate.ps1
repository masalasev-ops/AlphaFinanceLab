# Apply EF migrations to the arena store, snapshot-first (rule 14). Migrates EXACTLY the file it
# snapshots by resolving ConnectionStrings:AlphaLab the same way snapshot-db.ps1 does and passing it
# via --connection (finding 119) - so the guarantee never depends on the design-time factory's
# compiled fallback staying in sync with an edited appsettings.
param([string]$Arena = 'sp500')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-AlphaLabConnection.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$cs = Resolve-AlphaLabConnectionString -Arena $Arena

# 1. Snapshot the SAME file we are about to migrate, first.
& (Join-Path $PSScriptRoot 'snapshot-db.ps1') -Arena $Arena

# 2. Apply migrations to that exact file.
Push-Location $repoRoot
try {
    # Gate on $LASTEXITCODE, NOT stderr: dotnet emits the NU1903 restore warning on stderr, which PS
    # 5.1 turns into a terminating NativeCommandError under EAP='Stop' when stderr is captured. Relax
    # EAP only around the native call, then throw iff the exit code is non-zero.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    dotnet dotnet-ef database update `
        --connection "$cs" `
        --project 'src/AlphaLab.Data' `
        --startup-project 'src/AlphaLab.Data'
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previousEap

    if ($code -ne 0) {
        throw "dotnet-ef database update failed with exit code $code."
    }
    Write-Host "Migration applied to arena '$Arena' at: $(Get-AlphaLabDataSourcePath -ConnectionString $cs)" -ForegroundColor Green
}
finally {
    Pop-Location
}
