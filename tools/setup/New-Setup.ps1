<#
.SYNOPSIS
    Builds a single-file installer (one exe) for Clode Myriagent from this checkout.
    ASCII only, Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
    The installer contains a self-contained publish of the desktop app (the .NET runtime is included, so the user does
    not have to install anything first) and a small .NET Framework 4.8 front end (tools/setup). Running it unpacks the
    app to a temporary folder and runs  Myriagent.exe --install-package , which installs it for the current user
    (%LOCALAPPDATA%\Programs\Myriagent, Start menu shortcut, uninstall entry; no administrator rights) and starts it.

    -Edition standalone (default): the single-computer edition. It never pairs with a hub, never syncs and has no
    update source; the settings pages for multi-device sync and collaborators are not shown (src/Myriagent/Core/AppEdition.cs).
    -Edition full: the normal app.

    The version is Major.Minor of src/Myriagent/Myriagent.csproj plus the number of commits of HEAD, the same
    rule as desktop releases. Outside a git checkout (for example a git archive) pass -Commit, -CommitCount and
    optionally -CommitTime.

    Steps: dotnet publish (Release, win-x64, self-contained) -> zip -> dotnet build tools/setup with the zip embedded.
    Output in -OutDir (default out\setup): the installer, and setup.json (file name, version, size, SHA-256).
    Exit code: 0 success; otherwise non-zero.
#>
[CmdletBinding()]
param(
    [ValidateSet('standalone', 'full')]
    [string]$Edition = 'standalone',
    [string]$OutDir = '',
    [string]$Commit = '',
    [int]$CommitCount = 0,
    [string]$CommitTime = ''
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

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$csproj = Join-Path $repo 'src\Myriagent\Myriagent.csproj'
$appExe = 'Myriagent.exe'

if (-not $Commit) { $Commit = Get-NativeOutput 'git' @('-C', $repo, 'rev-parse', 'HEAD') }
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw ('unexpected commit: {0}' -f $Commit) }
if ($CommitCount -le 0) { $CommitCount = [int](Get-NativeOutput 'git' @('-C', $repo, 'rev-list', '--count', 'HEAD')) }
if ($CommitCount -lt 1 -or $CommitCount -gt 65534) { throw ('commit count out of range: {0}' -f $CommitCount) }
if (-not $CommitTime) {
    $CommitTime = Get-NativeOutput 'git' @('-C', $repo, 'log', '-1', '--format=%cI', $Commit)
}
if ($CommitTime -notmatch '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(Z|[+-]\d\d:\d\d)$') { throw ('unexpected commit time: {0}' -f $CommitTime) }

[xml]$project = [System.IO.File]::ReadAllText($csproj)
$baseVersion = [string]($project.Project.PropertyGroup | ForEach-Object { $_.SelectSingleNode('Version') } | Where-Object { $_ } | ForEach-Object { $_.InnerText } | Select-Object -First 1)
if ($baseVersion -notmatch '^(\d+)\.(\d+)\.0$') { throw ('the csproj <Version> must be Major.Minor.0: {0}' -f $baseVersion) }
$version = '{0}.{1}.{2}' -f $Matches[1], $Matches[2], $CommitCount
$release = '{0}-{1}' -f $version, $Commit.Substring(0, 7)

if (-not $OutDir) { $OutDir = Join-Path $repo 'out\setup' }
$work = Join-Path $repo 'out\setup-work'
foreach ($dir in @($OutDir, $work)) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    [void][System.IO.Directory]::CreateDirectory($dir)
}
$publish = Join-Path $work 'publish'
$zip = Join-Path $work 'payload.zip'
$setupBin = Join-Path $work 'setup-bin'

Write-Step ('publish {0} ({1}, self-contained)' -f $release, $Edition)
# PathMap: the build folder does not end up in the binaries and symbols
$publishArgs = @('publish', $csproj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish,
    ('-p:Version={0}' -f $version), ('-p:SourceRevisionId={0}' -f $Commit), ('-p:MyriagentCommitTime={0}' -f $CommitTime),
    ('-p:PathMap={0}\=/_/' -f $repo.TrimEnd('\')), '-nodeReuse:false', '-p:UseSharedCompilation=false')
if ($Edition -eq 'standalone') { $publishArgs += '-p:AppEdition=standalone' }
Invoke-Checked 'dotnet' $publishArgs
if (-not (Test-Path -LiteralPath (Join-Path $publish $appExe))) { throw ('publish did not produce {0}' -f $appExe) }

Write-Step 'zip'
# entry names with forward slashes, sorted (Windows PowerShell 5.1's CreateFromDirectory writes backslashes)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$prefix = $publish.TrimEnd('\') + '\'
$files = [System.IO.Directory]::GetFiles($publish, '*', [System.IO.SearchOption]::AllDirectories) | Sort-Object
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $entry = $file.Substring($prefix.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
}

Write-Step 'build the installer'
Invoke-Checked 'dotnet' @('build', (Join-Path $PSScriptRoot 'Setup.csproj'), '-c', 'Release', '-o', $setupBin,
    ('-p:PayloadZip={0}' -f $zip), ('-p:Version={0}' -f $version), '-nodeReuse:false', '-p:UseSharedCompilation=false')
$built = Join-Path $setupBin 'MyriagentSetup.exe'
if (-not (Test-Path -LiteralPath $built)) { throw 'the installer was not built' }

$name = 'Myriagent-Setup-{0}.exe' -f $version
$target = Join-Path $OutDir $name
[System.IO.File]::Copy($built, $target, $true)
$sha = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $target).Length
$info = [ordered]@{
    file      = $name
    version   = $version
    release   = $release
    commit    = $Commit
    commitAt  = $CommitTime
    edition   = $Edition
    size      = $size
    sha256    = $sha
}
[System.IO.File]::WriteAllText((Join-Path $OutDir 'setup.json'), ($info | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ('installer {0} ({1:N1} MB)' -f $target, ($size / 1MB))
Write-Host ('  sha256 {0}' -f $sha)
exit 0
