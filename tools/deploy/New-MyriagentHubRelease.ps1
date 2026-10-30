<#
.SYNOPSIS
    Packages a published MyriagentHub folder into a release: zip, manifest (per-file SHA-256), hashes, deploy tools.
    ASCII only; runs on Windows PowerShell 5.1 and PowerShell 7 (the build job uses pwsh on windows-latest).

.DESCRIPTION
    Output in OutDir:
      myriagenthub-<release>.zip             the publish folder, entries sorted, forward slashes
      myriagenthub-<release>.manifest.json   {schema,name,release,version,build,commit,createdAt,zip,zipSha256,
                                          fileCount,totalBytes,files[{path,size,sha256}],deployTools[{name,sha256}]}
      <each deploy tool>                 copied flat (Install-MyriagentHubRelease.ps1)
    release = <Version without +metadata>-<Build>; Build is the first 7 characters of the commit hash (lower case),
    the value MyriagentHub reports in /healthz and status\running.json (published with -p:MyriagentHubBuild=<Build>).

    When GITHUB_OUTPUT is set, appends release, version, build, zip_name, zip_sha256, manifest_name,
    manifest_sha256 and installer_sha256 for the next jobs. Always prints one JSON summary line.

.EXAMPLE
    pwsh tools/deploy/New-MyriagentHubRelease.ps1 -PublishDir out/publish -OutDir out/release -Version 0.3.0 -Build 1a2b3c4 `
        -Commit $env:GITHUB_SHA -DeployTools tools/deploy/Install-MyriagentHubRelease.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Build,
    [string]$Commit = '',
    [string[]]$DeployTools = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$cleanVersion = ($Version -split '\+')[0].Trim()
if ($cleanVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]{0,39}$') { throw "Invalid version: $Version" }
if ($Build -cnotmatch '^[0-9a-f]{7}$') { throw "Invalid build (expected the 7-character lower-case commit hash): $Build" }
$release = '{0}-{1}' -f $cleanVersion, $Build

# Normalize with Get-Item (DirectoryInfo.FullName): the FullName of every file below uses the same normalization.
# Resolve-Path keeps 8.3 short names while FullName expands them (GitHub windows runners have
# TEMP=C:\Users\RUNNER~1\...), and a Resolve-Path prefix then cuts every relative path at the wrong place.
$PublishDir = (Get-Item -LiteralPath $PublishDir -Force).FullName.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $PublishDir 'MyriagentHub.exe'))) { throw "MyriagentHub.exe not found in $PublishDir" }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$OutDir = (Get-Item -LiteralPath $OutDir -Force).FullName.TrimEnd('\')
if ($OutDir.StartsWith($PublishDir + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'OutDir must not be inside PublishDir' }

# Files, sorted ordinally by relative path with forward slashes (deterministic manifest and zip order).
$prefix = $PublishDir + '\'
$entries = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Force | ForEach-Object {
        if (-not $_.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "File is not under the publish folder after normalization: $($_.FullName)" }
        [pscustomobject]@{ Full = $_.FullName; Rel = $_.FullName.Substring($prefix.Length).Replace('\', '/'); Size = $_.Length }
    })
if (@($entries | Where-Object { $_.Rel -ceq 'MyriagentHub.exe' }).Count -ne 1) { throw 'MyriagentHub.exe is missing from the computed file list' }
$sorted = New-Object System.Collections.Generic.List[object]
foreach ($e in $entries) { $sorted.Add($e) }
$sorted.Sort([System.Comparison[object]] { param($a, $b) [string]::CompareOrdinal($a.Rel, $b.Rel) })

$files = New-Object System.Collections.Generic.List[object]
$total = [int64]0
foreach ($e in $sorted) {
    $files.Add([ordered]@{ path = $e.Rel; size = $e.Size; sha256 = (Get-Sha256 $e.Full) })
    $total += $e.Size
}

# Zip with forward-slash entry names in the same order.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipName = 'myriagenthub-{0}.zip' -f $release
$zipPath = Join-Path $OutDir $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$stream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($e in $sorted) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $e.Full, $e.Rel, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }
$zipSha = Get-Sha256 $zipPath

# Deploy tools travel next to the zip so a package can be installed without a checkout.
$tools = New-Object System.Collections.Generic.List[object]
foreach ($t in @($DeployTools | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {
    $src = (Resolve-Path -LiteralPath $t.Trim()).Path
    $name = Split-Path $src -Leaf
    Copy-Item -LiteralPath $src -Destination (Join-Path $OutDir $name) -Force
    $tools.Add([ordered]@{ name = $name; sha256 = (Get-Sha256 (Join-Path $OutDir $name)) })
}

$manifest = [ordered]@{
    schema      = 1
    name        = 'MyriagentHub'
    release     = $release
    version     = $cleanVersion
    build       = $Build
    commit      = $Commit
    createdAt   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    zip         = $zipName
    zipSha256   = $zipSha
    fileCount   = $files.Count
    totalBytes  = $total
    files       = $files.ToArray()
    deployTools = $tools.ToArray()
}
$manifestName = 'myriagenthub-{0}.manifest.json' -f $release
$manifestPath = Join-Path $OutDir $manifestName
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), $utf8)
$manifestSha = Get-Sha256 $manifestPath
$installerSha = ''
foreach ($t in $tools) { if ($t.name -eq 'Install-MyriagentHubRelease.ps1') { $installerSha = $t.sha256 } }

$outputs = [ordered]@{
    release = $release; version = $cleanVersion; build = $Build; zip_name = $zipName; zip_sha256 = $zipSha
    manifest_name = $manifestName; manifest_sha256 = $manifestSha; installer_sha256 = $installerSha
}
if ($env:GITHUB_OUTPUT) {
    $lines = @($outputs.Keys | ForEach-Object { '{0}={1}' -f $_, $outputs[$_] })
    [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, (($lines -join "`n") + "`n"), $utf8)
}
Write-Host ('RELEASE ' + ($outputs | ConvertTo-Json -Compress))
Write-Host ('{0} files, {1:N1} MB, zip sha256 {2}' -f $files.Count, ($total / 1MB), $zipSha)
