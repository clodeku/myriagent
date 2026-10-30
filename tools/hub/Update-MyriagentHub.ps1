<#
.SYNOPSIS
    Builds MyriagentHub from this repository and installs it on this computer (first install and every update).
    ASCII only, Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
    Run as Administrator on the hub computer, from a checkout of this repository, after tools/hub/Setup-MyriagentHub.ps1:

        powershell -NoProfile -ExecutionPolicy Bypass -File tools\hub\Update-MyriagentHub.ps1

    1. Reads the commit of the checkout (git rev-parse HEAD); the build is its first 7 characters.
    2. dotnet publish src/MyriagentHub (Release, win-x64, self-contained) into a temporary folder.
    3. tools/deploy/New-MyriagentHubRelease.ps1 packages it (zip, manifest with per-file SHA-256).
    4. tools/deploy/Install-MyriagentHubRelease.ps1 installs it: admin preflight, drain, stop, stopped-state database copy,
       junction switch, start, 60-second health gate, automatic rollback to the previous release.
    The exit code is the one of Install-MyriagentHubRelease.ps1 (0 = deployed and healthy, 1 = refused before the service
    was touched, 2 = rolled back, 4 = administrator action needed). The temporary folder is removed on success.

    -DryRun stops after packaging (prints where the package is) and changes nothing on this computer.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Services\MyriagentHub',
    [string]$DataRoot = 'C:\ProgramData\MyriagentHub',
    [string]$ServiceName = 'MyriagentHub',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Write-Step([string]$Message) { Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message) }

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw ('{0} {1} failed with exit code {2}' -f $FilePath, ($Arguments -join ' '), $LASTEXITCODE) }
}

function Get-NativeOutput([string]$FilePath, [string[]]$Arguments) {
    $out = & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('{0} {1} failed with exit code {2}' -f $FilePath, ($Arguments -join ' '), $LASTEXITCODE) }
    return ([string]($out | Select-Object -Last 1)).Trim()
}

$principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $DryRun -and -not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run as Administrator (or add -DryRun to only build and package).'
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'src\MyriagentHub\MyriagentHub.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw ('not a checkout of this repository: {0} is missing' -f $project) }
$commit = Get-NativeOutput 'git' @('-C', $repo, 'rev-parse', 'HEAD')
if ($commit -notmatch '^[0-9a-f]{40}$') { throw ('unexpected commit: {0}' -f $commit) }
$short = $commit.Substring(0, 7)
$dirty = & git -C $repo status --porcelain
if ($dirty) { Write-Warning 'the checkout has uncommitted changes: the release is labelled with the last commit but contains them' }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('myriagenthub-update-{0}-{1:yyyyMMdd-HHmmss}' -f $short, (Get-Date))
[void][System.IO.Directory]::CreateDirectory($work)
$publish = Join-Path $work 'publish'
$out = Join-Path $work 'release'

Write-Step ('build {0}' -f $short)
Invoke-Checked 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish,
    ('-p:MyriagentHubBuild={0}' -f $short), ('-p:SourceRevisionId={0}' -f $commit), '-nodeReuse:false')
$version = Get-NativeOutput 'dotnet' @('msbuild', $project, '-getProperty:Version')
if (-not $version) { $version = '0.0.0' }

Write-Step 'package (manifest, zip, hashes)'
$outputFile = Join-Path $work 'release-outputs.txt'
$previousOutput = $env:GITHUB_OUTPUT
$env:GITHUB_OUTPUT = $outputFile
try {
    & (Join-Path $repo 'tools\deploy\New-MyriagentHubRelease.ps1') -PublishDir $publish -OutDir $out -Version $version -Build $short -Commit $commit `
        -DeployTools (Join-Path $repo 'tools\deploy\Install-MyriagentHubRelease.ps1')
}
finally { $env:GITHUB_OUTPUT = $previousOutput }
$values = @{}
foreach ($line in [System.IO.File]::ReadAllLines($outputFile)) {
    $i = $line.IndexOf('=')
    if ($i -gt 0) { $values[$line.Substring(0, $i)] = $line.Substring($i + 1) }
}
foreach ($key in @('release', 'zip_name', 'zip_sha256', 'manifest_name', 'manifest_sha256')) {
    if (-not $values.ContainsKey($key) -or -not $values[$key]) { throw ('New-MyriagentHubRelease.ps1 did not report {0}' -f $key) }
}
Write-Host ('release {0} in {1}' -f $values['release'], $out)
if ($DryRun) {
    Write-Step 'dry run: nothing was installed'
    exit 0
}

Write-Step ('install {0}' -f $values['release'])
& (Join-Path $out 'Install-MyriagentHubRelease.ps1') -Zip (Join-Path $out $values['zip_name']) -Manifest (Join-Path $out $values['manifest_name']) `
    -ZipSha256 $values['zip_sha256'] -ManifestSha256 $values['manifest_sha256'] -ExpectedBuild $short `
    -InstallRoot $InstallRoot -DataRoot $DataRoot -ServiceName $ServiceName
$code = $LASTEXITCODE
if ($code -eq 0) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    Write-Step ('{0} is running' -f $values['release'])
}
else { Write-Host ('Install-MyriagentHubRelease.ps1 exited with {0}; the package is kept in {1}' -f $code, $work) }
exit $code
