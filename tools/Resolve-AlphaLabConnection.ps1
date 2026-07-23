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

function ConvertTo-AlphaLabNativePath {
    # Mirrors DbPathResolver.ResolvePath's separator normalization (v1.9.36) so this script and the C#
    # resolver agree on WHICH FILE they mean. Without it the two diverge on Linux: a backslash template
    # resolves to a real multi-segment path in C# but to a single filename containing backslashes here,
    # so migrate.ps1 would snapshot and migrate a different file than the Worker opens - the finding-119
    # class of bug that sharing this resolver exists to prevent. URI data sources (file:...) are URIs,
    # not paths - the grammar mandates '/', so they are left alone, exactly as the C# side does.
    param([Parameter(Mandatory)][string]$Path)

    if ($Path.StartsWith('file:', [StringComparison]::OrdinalIgnoreCase)) { return $Path }

    $sep = [System.IO.Path]::DirectorySeparatorChar
    return $Path.Replace('\', $sep).Replace('/', $sep)
}

function Get-AlphaLabDataSourcePath {
    param([Parameter(Mandatory)][string]$ConnectionString)

    foreach ($part in $ConnectionString.Split(';')) {
        $kv = $part.Split('=', 2)
        if ($kv.Length -eq 2 -and $kv[0].Trim().ToLowerInvariant() -in @('data source', 'datasource')) {
            return ConvertTo-AlphaLabNativePath -Path ($kv[1].Trim().Trim('"'))
        }
    }
    throw "No 'Data Source' found in connection string: $ConnectionString"
}

function Test-AlphaLabStoreExists {
    # Rule-14 fail-CLOSED existence check (finding 265). Test-Path returns FALSE both for a genuinely
    # absent file and for one whose existence cannot be determined (a transient access-denied from an
    # antivirus/indexer lock) - and routing the second case into a "fresh install, skip the snapshot"
    # branch is exactly how a store gets migrated without its snapshot. So: absence is only believed
    # when the PARENT DIRECTORY can be enumerated and the file is not in the listing; a missing parent
    # is a genuinely fresh install; any error determining either is a THROW, never a false.
    param([Parameter(Mandatory)][string]$DbPath)

    $parent = Split-Path -Parent $DbPath
    $leaf = Split-Path -Leaf $DbPath
    if (-not [System.IO.Directory]::Exists($parent)) { return $false }  # fresh install: no arena dir at all
    try {
        $names = [System.IO.Directory]::GetFiles($parent) | ForEach-Object { [System.IO.Path]::GetFileName($_) }
        return $names -contains $leaf
    }
    catch {
        $message = "Cannot determine whether the store exists at '{0}' ({1}) - refusing to guess: a transient lock read as absent is how a store gets migrated without its rule-14 snapshot. Retry." -f $DbPath, $_.Exception.Message
        throw $message
    }
}
