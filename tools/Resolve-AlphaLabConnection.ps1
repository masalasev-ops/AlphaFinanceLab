# Shared connection-string resolution for the AlphaLab ops scripts. Dot-sourced by both
# snapshot-db.ps1 and migrate.ps1 so migrate.ps1 migrates EXACTLY the file snapshot-db.ps1
# snapshots (finding 119). Resolves ConnectionStrings:AlphaLab from the Worker's appsettings.json,
# replacing {Arena.Id} (from -Arena) and {LocalAppData} (known-folders API, never an env var - D67).

function Resolve-AlphaLabConnectionString {
    param([Parameter(Mandatory)][string]$Arena)

    $repoRoot = Split-Path -Parent $PSScriptRoot
    $appsettings = Join-Path $repoRoot 'src/AlphaLab.Worker/appsettings.json'
    if (-not (Test-Path $appsettings)) {
        throw "Worker appsettings.json not found at $appsettings"
    }

    $json = Get-Content -Raw -Path $appsettings | ConvertFrom-Json
    $cs = $json.ConnectionStrings.AlphaLab
    if ([string]::IsNullOrWhiteSpace($cs)) {
        throw "ConnectionStrings:AlphaLab is missing from $appsettings"
    }

    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    return $cs.Replace('{Arena.Id}', $Arena).Replace('{LocalAppData}', $localAppData)
}

function Get-AlphaLabDataSourcePath {
    param([Parameter(Mandatory)][string]$ConnectionString)

    foreach ($part in $ConnectionString.Split(';')) {
        $kv = $part.Split('=', 2)
        if ($kv.Length -eq 2 -and $kv[0].Trim().ToLowerInvariant() -in @('data source', 'datasource')) {
            return $kv[1].Trim()
        }
    }
    throw "No 'Data Source' found in connection string: $ConnectionString"
}
