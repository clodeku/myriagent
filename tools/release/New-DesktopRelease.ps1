<#
.SYNOPSIS
    Builds, packages and signs one desktop release of Clode Myriagent from this checkout; with -Submit also hands it to the
    MyriagentHub on this computer, which then offers it to every paired computer (app self-update).
    ASCII only, Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
    Prerequisites (docs/release.md):
      - your own signing key, created once on this computer:
            dotnet run --project tools/release-tool -c Release -- keygen --dpapi-file "%LOCALAPPDATA%\MyriagentRelease\desktop-release-key.dpapi"
        The private key is DPAPI-encrypted for this Windows user; only this user on this computer can sign with it.
      - the printed public line  new ReleaseKey("<kid>", "<spki>")  added to ReleaseKeys.Production in
        src/Myriagent.Protocol/Update/ReleaseKeys.cs, committed, and a desktop and hub built from that commit installed
        (the desktop and the hub trust only the keys built into them).

    Steps:
      1. myriagent-release (tools/release-tool) computes the version: Major.Minor of src/Myriagent/Myriagent.csproj plus
         the number of commits of HEAD (git rev-list --count HEAD), so every commit has a higher version.
      2. dotnet publish src/Myriagent (Release, win-x64, framework-dependent); check-assembly makes sure no test key is
         embedded; package (zip + manifest); sign with -KeyFile; verify against ReleaseKeys.Production.
      3. Prints the three files and their SHA-256. With -Submit (run as Administrator on the hub computer) it calls
         tools/release/Publish-DesktopRelease.ps1, which copies them into DataRoot\admin-inbox\app-release and waits
         for the service's receipt.
    Exit code: 0 success (and, with -Submit, the service accepted the release); otherwise non-zero.
#>
[CmdletBinding()]
param(
    [string]$KeyFile = (Join-Path $env:LOCALAPPDATA 'MyriagentRelease\desktop-release-key.dpapi'),
    [string]$OutDir = '',
    [switch]$Submit,
    [string]$DataRoot = 'C:\ProgramData\MyriagentHub'
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

function Read-OutputFile([string]$Path) {
    $values = @{}
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
            $i = $line.IndexOf('=')
            if ($i -gt 0) { $values[$line.Substring(0, $i)] = $line.Substring($i + 1) }
        }
    }
    return $values
}

if (-not (Test-Path -LiteralPath $KeyFile)) {
    throw ('signing key file not found: {0} (create it once with: dotnet run --project tools/release-tool -c Release -- keygen --dpapi-file <file>; see docs/release.md)' -f $KeyFile)
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$commit = Get-NativeOutput 'git' @('-C', $repo, 'rev-parse', 'HEAD')
if ($commit -notmatch '^[0-9a-f]{40}$') { throw ('unexpected commit: {0}' -f $commit) }
if (& git -C $repo status --porcelain) { Write-Warning 'the checkout has uncommitted changes: the release is labelled with the last commit but contains them' }
if (-not $OutDir) { $OutDir = Join-Path $repo 'out\desktop-release' }
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
$publish = Join-Path $repo 'out\desktop-publish'
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }

$outputFile = Join-Path $repo 'out\desktop-outputs.txt'
[void][System.IO.Directory]::CreateDirectory((Join-Path $repo 'out'))
if (Test-Path -LiteralPath $outputFile) { Remove-Item -LiteralPath $outputFile -Force }
$previousOutput = $env:GITHUB_OUTPUT
$env:GITHUB_OUTPUT = $outputFile
Push-Location -LiteralPath $repo
try {
    Write-Step 'version'
    $tool = @('run', '--project', 'tools/release-tool', '-c', 'Release', '--no-build', '--')
    Invoke-Checked 'dotnet' @('build', 'tools/release-tool', '-c', 'Release', '-nodeReuse:false')
    $count = Get-NativeOutput 'git' @('rev-list', '--count', 'HEAD')
    Invoke-Checked 'dotnet' ($tool + @('version', '--csproj', 'src/Myriagent/Myriagent.csproj', '--count', $count, '--commit', $commit))
    $values = Read-OutputFile $outputFile
    $release = $values['release']
    $version = $values['version']
    if (-not $release -or -not $version) { throw 'myriagent-release version did not report release and version' }

    Write-Step ('build {0}' -f $release)
    Invoke-Checked 'dotnet' @('publish', 'src/Myriagent/Myriagent.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $publish,
        ('-p:Version={0}' -f $version), ('-p:SourceRevisionId={0}' -f $commit), '-nodeReuse:false')
    Invoke-Checked 'dotnet' ($tool + @('check-assembly', '--publish-dir', $publish, '--release', $release, '--commit', $commit))

    Write-Step 'package, sign, verify'
    Invoke-Checked 'dotnet' ($tool + @('package', '--publish-dir', $publish, '--release', $release, '--commit', $commit, '--out', $OutDir))
    $values = Read-OutputFile $outputFile
    $manifest = Join-Path $OutDir $values['manifest_name']
    $sig = Join-Path $OutDir ('myriagent-{0}.manifest.sig' -f $release)
    Invoke-Checked 'dotnet' ($tool + @('sign', '--manifest', $manifest, '--out', $sig, '--key-file', $KeyFile))
    Invoke-Checked 'dotnet' ($tool + @('verify', '--dir', $OutDir, '--release', $release))
}
finally {
    Pop-Location
    $env:GITHUB_OUTPUT = $previousOutput
}

$values = Read-OutputFile $outputFile
foreach ($key in @('zip_name', 'zip_sha256', 'manifest_name', 'manifest_sha256', 'sig_name', 'sig_sha256', 'kid')) {
    if (-not $values.ContainsKey($key) -or -not $values[$key]) { throw ('myriagent-release did not report {0}' -f $key) }
}
Write-Host ('release {0}, signing kid {1}, files in {2}' -f $release, $values['kid'], $OutDir)
Write-Host ('  {0}  {1}' -f $values['zip_sha256'], $values['zip_name'])
Write-Host ('  {0}  {1}' -f $values['manifest_sha256'], $values['manifest_name'])
Write-Host ('  {0}  {1}' -f $values['sig_sha256'], $values['sig_name'])
$publishArgs = @('-ArtifactDir', $OutDir, '-Release', $release, '-ZipSha256', $values['zip_sha256'], '-ManifestSha256', $values['manifest_sha256'],
    '-SigSha256', $values['sig_sha256'], '-DataRoot', $DataRoot)
if (-not $Submit) {
    Write-Host 'To hand it to the hub, copy the folder to the hub computer and run there (as Administrator):'
    Write-Host ('  powershell -NoProfile -ExecutionPolicy Bypass -File tools\release\Publish-DesktopRelease.ps1 {0}' -f ($publishArgs -join ' '))
    exit 0
}
Write-Step 'submit to the hub on this computer'
& (Join-Path $PSScriptRoot 'Publish-DesktopRelease.ps1') @publishArgs
exit $LASTEXITCODE
