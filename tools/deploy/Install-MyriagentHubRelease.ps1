<#
.SYNOPSIS
    Installs one MyriagentHub release on the hub computer: hash checks, admin preflight, drain, stop, stopped-state
    database copy, junction switch, start, 60-second health gate, automatic rollback and retention.
    ASCII only, Windows PowerShell 5.1.

.DESCRIPTION
    Run as an administrator on the hub computer. tools/hub/Update-MyriagentHub.ps1 builds and packages a release and
    then calls this script; it can also be run directly with a package made by tools/deploy/New-MyriagentHubRelease.ps1.

    Install mode:  -Zip <zip> -Manifest <manifest.json> -ZipSha256 <hex> -ManifestSha256 <hex> [-ExpectedBuild <build>]
    Switch mode:   -SwitchTo <release>   (re-activate a release that is still in InstallRoot\releases)

    Exit codes:
      0  deployed (or already current) and healthy
      1  refused before the service was touched (bad input, hash mismatch, preflight failed, another deploy running)
      2  deployment failed; the previous release is running again and healthy (or, on a first deployment, the
         service was left stopped)
      4  rollback failed or the service state is unknown: administrator action needed (see docs/hub-setup.md)

    Contract with the service (implemented by src/MyriagentHub):
      MyriagentHub.exe admin preflight --data <DataRoot>      exit 0 = this release can run on the current data
      DataRoot\admin-inbox\deploy\<id>.json             {"command":"drain",...}; the service acknowledges by writing
                                                          DataRoot\status\drain.json {"id":"<id>",...} (best effort);
                                                          without an answer within DrainWaitSec this script withdraws
                                                          (deletes) its request before stopping, and the service only
                                                          runs deploy drains written after its process started;
                                                          a deploy drain expires after Hub:Deploy:DrainMaxSeconds
                                                          (default 300 s, far above DrainWaitSec + StopTimeoutSec)
                                                          unless the service has received a stop request
      DataRoot\status\running.json                      {"build":"<build>","version":"<version>",...} after start
      DataRoot\status\selftest.json                     {"ok":true,...} (or "status":"ok"/"pass") after start
      GET https://<domain>/healthz (loopback)            200 {"status":"ok","build":"<build>"}
      <build> is always the first 7 characters of the commit hash (lower case): the manifest, running.json and
      healthz compare the same 7 characters. Without -HealthUrl the domain and port come from
      DataRoot\config\appsettings.Production.json (Hub:ServerDomain, Hub:HttpsPort).

    Before the service is touched, the data volume must hold the stopped-state database copy and still keep MinFreeGB
    (default 40, the same floor as the service's blobs and backups) free; otherwise exit 1 'refused'. Pre-deploy
    copies are kept for the newest KeepPredeploy (default 2) deployments.

    -ServiceShim <file> replaces the service operations (Get-HubState, Stop-Hub, Start-Hub) for dry runs on a
    development machine (tools/deploy/Test-MyriagentHubDeploy.ps1).
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Install')][string]$Zip,
    [Parameter(Mandatory = $true, ParameterSetName = 'Install')][string]$Manifest,
    [Parameter(Mandatory = $true, ParameterSetName = 'Install')][string]$ZipSha256,
    [Parameter(Mandatory = $true, ParameterSetName = 'Install')][string]$ManifestSha256,
    [Parameter(ParameterSetName = 'Install')][string]$ExpectedBuild = '',
    [Parameter(Mandatory = $true, ParameterSetName = 'Switch')][string]$SwitchTo,
    [string]$InstallRoot = 'C:\Services\MyriagentHub',
    [string]$DataRoot = 'C:\ProgramData\MyriagentHub',
    [string]$ServiceName = 'MyriagentHub',
    [string]$HealthUrl = '',
    [string]$HealthResolve = '',
    [int]$HealthTimeoutSec = 60,
    [int]$StopTimeoutSec = 30,
    [int]$StartTimeoutSec = 30,
    [int]$DrainWaitSec = 10,
    [int]$PreflightTimeoutSec = 180,
    [int]$KeepReleases = 3,
    [int]$KeepPredeploy = 2,
    [int]$MinFreeGB = 40,
    [string]$ServiceShim = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$ProgressPreference = 'SilentlyContinue'
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Without -HealthUrl / -HealthResolve the loopback health check uses Hub:ServerDomain and Hub:HttpsPort of the service config.
if (-not $HealthUrl -or -not $HealthResolve) {
    $hubDomain = $null
    $hubPort = 443
    $hubConfig = Join-Path $DataRoot 'config\appsettings.Production.json'
    if (Test-Path -LiteralPath $hubConfig) {
        try {
            $cfg = [System.IO.File]::ReadAllText($hubConfig) | ConvertFrom-Json
            if ($cfg.PSObject.Properties['Hub']) {
                if ($cfg.Hub.PSObject.Properties['ServerDomain']) { $hubDomain = [string]$cfg.Hub.ServerDomain }
                if ($cfg.Hub.PSObject.Properties['HttpsPort']) { $hubPort = [int]$cfg.Hub.HttpsPort }
            }
        }
        catch { $hubDomain = $null }
    }
    if (-not $hubDomain) { Write-Host ('ERROR no -HealthUrl given and no Hub:ServerDomain in {0}' -f $hubConfig); exit 1 }
    if (-not $HealthUrl) { $HealthUrl = 'https://{0}:{1}/healthz' -f $hubDomain, $hubPort }
    if (-not $HealthResolve) { $HealthResolve = '{0}:{1}:127.0.0.1' -f $hubDomain, $hubPort }
}

# ------------------------------------------------------------------------------------------------
# Logging and small helpers
# ------------------------------------------------------------------------------------------------
$script:LogFile = $null
function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '[{0:yyyy-MM-dd HH:mm:ss}] {1,-5} {2}' -f (Get-Date), $Level, $Message
    Write-Host $line
    if ($script:LogFile) { try { [System.IO.File]::AppendAllText($script:LogFile, $line + [Environment]::NewLine, $Utf8NoBom) } catch { } }
}

function Get-FileSha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Read-JsonFile([string]$Path) {
    return ([System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json)
}

function Get-Prop($Object, [string]$Name) {
    # StrictMode-safe property read; returns $null when the property does not exist.
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Invoke-Quiet {
    # Runs a native command with stderr captured as text (Windows PowerShell 5.1 would otherwise turn stderr into
    # terminating errors under ErrorActionPreference=Stop). Returns @{ ExitCode; Output }.
    param([string]$FilePath, [string[]]$ArgumentList)
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $FilePath @ArgumentList 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($out -join [Environment]::NewLine) }
    }
    finally { $ErrorActionPreference = $saved }
}

$Cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
$Curl = Join-Path $env:SystemRoot 'System32\curl.exe'

function Remove-DirectoryTree([string]$Path) {
    # rmdir /s /q removes junctions inside the tree without following them.
    if (Test-Path -LiteralPath $Path) {
        $r = Invoke-Quiet $Cmd @('/c', 'rmdir', '/s', '/q', $Path)
        if (Test-Path -LiteralPath $Path) { throw "Could not delete $Path : $($r.Output)" }
    }
}

# ------------------------------------------------------------------------------------------------
# Service operations (replaceable by -ServiceShim for dry runs)
# ------------------------------------------------------------------------------------------------
function Get-HubState {
    $s = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $s) { return 'Missing' }
    return [string]$s.Status
}

function Stop-Hub {
    $s = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $s -or $s.Status -eq 'Stopped') { return }
    if ($s.Status -ne 'StopPending') { $s.Stop() }
    $s.WaitForStatus('Stopped', [TimeSpan]::FromSeconds($StopTimeoutSec))
}

function Start-Hub {
    $s = Get-Service -Name $ServiceName -ErrorAction Stop
    if ($s.Status -eq 'Running') { return }
    $s.Start()
    $s.WaitForStatus('Running', [TimeSpan]::FromSeconds($StartTimeoutSec))
}

if ($ServiceShim) {
    if (-not (Test-Path -LiteralPath $ServiceShim)) { throw "ServiceShim not found: $ServiceShim" }
    . $ServiceShim
}

# ------------------------------------------------------------------------------------------------
# Paths, junction, history
# ------------------------------------------------------------------------------------------------
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot).TrimEnd('\')
$ReleasesDir = Join-Path $InstallRoot 'releases'
$CurrentLink = Join-Path $InstallRoot 'current'
$HistoryFile = Join-Path $InstallRoot 'deploy-history.json'
$StatusDir = Join-Path $DataRoot 'status'

function Get-CurrentRelease {
    if (-not (Test-Path -LiteralPath $CurrentLink)) { return $null }
    $item = Get-Item -LiteralPath $CurrentLink -Force
    if (-not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { throw "$CurrentLink is a real folder, not a junction; refusing to continue." }
    $target = Get-Prop $item 'Target'
    if ($target -is [array]) { $target = $target[0] }
    if (-not $target) { throw "Cannot read the target of $CurrentLink" }
    $target = ([string]$target).TrimEnd('\')
    if ($target.StartsWith('\??\')) { $target = $target.Substring(4) }
    $parent = Split-Path $target -Parent
    if ($parent -ine $ReleasesDir) { throw "$CurrentLink points outside $ReleasesDir : $target" }
    return (Split-Path $target -Leaf)
}

function Set-CurrentRelease([string]$Release) {
    if (Test-Path -LiteralPath $CurrentLink) {
        $item = Get-Item -LiteralPath $CurrentLink -Force
        if (-not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { throw "$CurrentLink is a real folder; refusing to delete it." }
        $r = Invoke-Quiet $Cmd @('/c', 'rmdir', $CurrentLink)
        if (Test-Path -LiteralPath $CurrentLink) { throw "Could not remove junction $CurrentLink : $($r.Output)" }
    }
    if (-not $Release) { return }
    $target = Join-Path $ReleasesDir $Release
    if (-not (Test-Path -LiteralPath (Join-Path $target 'MyriagentHub.exe'))) { throw "Release folder is incomplete: $target" }
    $r = Invoke-Quiet $Cmd @('/c', 'mklink', '/J', $CurrentLink, $target)
    if ($r.ExitCode -ne 0) { throw "mklink /J failed: $($r.Output)" }
    $now = Get-CurrentRelease
    if ($now -ne $Release) { throw "Junction verification failed: current -> $now, expected $Release" }
}

function Get-History {
    if (-not (Test-Path -LiteralPath $HistoryFile)) { return @() }
    try { return @(Read-JsonFile $HistoryFile) } catch { Write-Log "deploy-history.json unreadable: $($_.Exception.Message)" 'WARN'; return @() }
}

function Add-History([string]$Release, [string]$Build, [string]$Result, [string]$Note) {
    $entry = [ordered]@{ release = $Release; build = $Build; result = $Result; note = $Note; at = (Get-Date).ToUniversalTime().ToString('o') }
    $all = @(@($entry) + @(Get-History) | Select-Object -First 50)
    $tmp = $HistoryFile + '.tmp'
    [System.IO.File]::WriteAllText($tmp, (ConvertTo-Json -InputObject $all -Depth 4), $Utf8NoBom)
    Move-Item -LiteralPath $tmp -Destination $HistoryFile -Force
}

function Get-ReleaseBuild([string]$Release) {
    $m = Join-Path $ReleasesDir ($Release + '.manifest.json')
    if (Test-Path -LiteralPath $m) { return [string](Get-Prop (Read-JsonFile $m) 'build') }
    return ''
}

function Get-ReleaseVersion([string]$Release) {
    $m = Join-Path $ReleasesDir ($Release + '.manifest.json')
    if (Test-Path -LiteralPath $m) { return [string](Get-Prop (Read-JsonFile $m) 'version') }
    return ''
}

# ------------------------------------------------------------------------------------------------
# Health gate
# ------------------------------------------------------------------------------------------------
function Test-StatusFile([string]$Name, [datetime]$SinceUtc) {
    $p = Join-Path $StatusDir $Name
    if (-not (Test-Path -LiteralPath $p)) { return @{ Ok = $false; Why = "$Name missing"; Data = $null } }
    $fi = Get-Item -LiteralPath $p
    if ($fi.LastWriteTimeUtc -lt $SinceUtc) { return @{ Ok = $false; Why = "$Name not rewritten since start"; Data = $null } }
    try { $data = Read-JsonFile $p } catch { return @{ Ok = $false; Why = "$Name unreadable"; Data = $null } }
    return @{ Ok = $true; Why = ''; Data = $data }
}

function Invoke-Healthz {
    $body = [System.IO.Path]::GetTempFileName()
    try {
        $a = @('-sS', '--max-time', '5', '-o', $body, '-w', '%{http_code}')
        if ($HealthUrl -like 'https:*') { $a += '-k' }   # loopback check of our own service; the verify job checks the certificate
        if ($HealthResolve -and $HealthResolve -ne 'none') { $a += @('--resolve', $HealthResolve) }
        $a += $HealthUrl
        $r = Invoke-Quiet $Curl $a
        $code = ($r.Output -split "`r?`n" | Select-Object -Last 1).Trim()
        $json = $null
        try { $json = [System.IO.File]::ReadAllText($body, [System.Text.Encoding]::UTF8) | ConvertFrom-Json } catch { }
        return @{ Code = $code; Json = $json }
    }
    finally { Remove-Item -LiteralPath $body -Force -ErrorAction SilentlyContinue }
}

function Wait-Healthy([string]$Build, [string]$Version, [datetime]$SinceUtc, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = ''
    do {
        $why = @()
        $state = Get-HubState
        if ($state -ne 'Running') { $why += "service $state" }
        $run = Test-StatusFile 'running.json' $SinceUtc
        if (-not $run.Ok) { $why += $run.Why }
        else {
            $rb = [string](Get-Prop $run.Data 'build')
            if ($Build -and $rb -ne $Build) { $why += "running.json build=$rb" }
            $rv = [string](Get-Prop $run.Data 'version')
            if ($Version -and $rv -and -not ($rv -eq $Version -or $rv.StartsWith($Version + '+'))) { $why += "running.json version=$rv" }
        }
        $st = Test-StatusFile 'selftest.json' $SinceUtc
        if (-not $st.Ok) { $why += $st.Why }
        else {
            $okFlag = Get-Prop $st.Data 'ok'
            $stStatus = [string](Get-Prop $st.Data 'status')
            if (-not (($okFlag -eq $true) -or (@('ok', 'pass', 'passed') -contains $stStatus.ToLowerInvariant()))) { $why += 'selftest not passing' }
        }
        $h = Invoke-Healthz
        if ($h.Code -ne '200') { $why += "healthz http=$($h.Code)" }
        else {
            $hs = [string](Get-Prop $h.Json 'status')
            $hb = [string](Get-Prop $h.Json 'build')
            if ($hs -ne 'ok') { $why += "healthz status=$hs" }
            if ($Build -and $hb -ne $Build) { $why += "healthz build=$hb" }
        }
        if ($why.Count -eq 0) { return @{ Ok = $true; Why = '' } }
        $now = $why -join '; '
        if ($now -ne $last) { Write-Log "waiting: $now"; $last = $now }
        if ($state -eq 'Stopped' -or $state -eq 'Missing') { break }   # crashed: no point in waiting longer
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return @{ Ok = $false; Why = $last }
}

# ------------------------------------------------------------------------------------------------
# Result handling
# ------------------------------------------------------------------------------------------------
$script:Summary = [ordered]@{ result = ''; exitCode = -1; release = ''; build = ''; previous = ''; detail = '' }
function Complete([int]$Code, [string]$Result, [string]$Detail) {
    $script:Summary.result = $Result
    $script:Summary.exitCode = $Code
    $script:Summary.detail = $Detail
    $level = 'INFO'
    if ($Code -ne 0) { $level = 'ERROR' }
    Write-Log ('{0} (exit {1}): {2}' -f $Result, $Code, $Detail) $level
    Write-Log ("RESULT {0}" -f (ConvertTo-Json -InputObject $script:Summary -Compress)) $level
    if ($env:GITHUB_STEP_SUMMARY) {
        $md = @('### MyriagentHub deploy', '', '| key | value |', '|---|---|')
        foreach ($k in $script:Summary.Keys) { $md += ('| {0} | {1} |' -f $k, ([string]$script:Summary[$k] -replace '\|', '/')) }
        try { [System.IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, ($md -join "`n") + "`n", $Utf8NoBom) } catch { }
    }
    if ($script:LockStream) { $script:LockStream.Dispose(); $script:LockStream = $null }
    exit $Code
}

# ------------------------------------------------------------------------------------------------
# Start: lock, log, inputs
# ------------------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $ReleasesDir)) { Write-Host "ERROR $ReleasesDir missing: run tools/hub/Setup-MyriagentHub.ps1 first"; exit 1 }
$script:LockStream = $null
try {
    $script:LockStream = New-Object System.IO.FileStream((Join-Path $InstallRoot 'deploy.lock'), [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None, 1, [System.IO.FileOptions]::DeleteOnClose)
}
catch { Write-Host 'ERROR another deployment holds deploy.lock'; exit 1 }

$logDir = Join-Path $InstallRoot 'logs'
if (Test-Path -LiteralPath $logDir) { $script:LogFile = Join-Path $logDir ('deploy-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date)) }

$previous = $null
try { $previous = Get-CurrentRelease } catch { Complete 1 'refused' $_.Exception.Message }
$script:Summary.previous = [string]$previous
Write-Log ('install={0} data={1} service={2} current={3} user={4}' -f $InstallRoot, $DataRoot, $ServiceName, $previous, [System.Security.Principal.WindowsIdentity]::GetCurrent().Name)

$release = $null; $build = ''; $version = ''
if ($PSCmdlet.ParameterSetName -eq 'Install') {
    foreach ($f in @($Zip, $Manifest)) { if (-not (Test-Path -LiteralPath $f)) { Complete 1 'refused' "missing input $f" } }
    $mh = Get-FileSha256 $Manifest
    if ($mh -ne $ManifestSha256.ToLowerInvariant()) { Complete 1 'refused' "manifest sha256 mismatch: expected $ManifestSha256 got $mh" }
    $zh = Get-FileSha256 $Zip
    if ($zh -ne $ZipSha256.ToLowerInvariant()) { Complete 1 'refused' "zip sha256 mismatch: expected $ZipSha256 got $zh" }
    $m = Read-JsonFile $Manifest
    $release = [string](Get-Prop $m 'release'); $build = [string](Get-Prop $m 'build'); $version = [string](Get-Prop $m 'version')
    $problems = @()
    if ((Get-Prop $m 'name') -ne 'MyriagentHub') { $problems += 'name is not MyriagentHub' }
    if ($release -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,79}$' -or $release.Contains('..')) { $problems += "bad release '$release'" }
    if ($build -cnotmatch '^[0-9a-f]{7}$') { $problems += "bad build '$build' (expected the 7-character commit hash)" }
    if ($ExpectedBuild -and $build -ne $ExpectedBuild) { $problems += "build $build is not the expected $ExpectedBuild" }
    $mz = [string](Get-Prop $m 'zipSha256')
    if ($mz -and $mz.ToLowerInvariant() -ne $zh) { $problems += 'manifest zipSha256 does not match the zip' }
    $files = @(Get-Prop $m 'files')
    if ($files.Count -eq 0) { $problems += 'manifest lists no files' }
    foreach ($e in $files) {
        $p = [string](Get-Prop $e 'path'); $h = [string](Get-Prop $e 'sha256')
        if (-not $p -or $p.StartsWith('/') -or $p.Contains('\') -or $p.Contains(':') -or ($p -split '/') -contains '..' -or ($p -split '/') -contains '.') { $problems += "bad path '$p'" }
        if ($h -notmatch '^[0-9a-fA-F]{64}$') { $problems += "bad sha256 for '$p'" }
    }
    if (@($files | Where-Object { (Get-Prop $_ 'path') -eq 'MyriagentHub.exe' }).Count -ne 1) { $problems += 'MyriagentHub.exe missing from manifest' }
    if ($problems.Count -gt 0) { Complete 1 'refused' ('manifest rejected: ' + (($problems | Select-Object -First 5) -join '; ')) }
}
else {
    $release = $SwitchTo
    if ($release -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,79}$' -or $release.Contains('..')) { Complete 1 'refused' "bad release '$release'" }
    if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $ReleasesDir $release) 'MyriagentHub.exe'))) { Complete 1 'refused' "release $release is not in $ReleasesDir" }
    $build = Get-ReleaseBuild $release; $version = Get-ReleaseVersion $release
}
$script:Summary.release = $release
$script:Summary.build = $build
Write-Log ('release={0} build={1} version={2}' -f $release, $build, $version)

if ($previous -and $previous -eq $release) {
    $h = Wait-Healthy $build $version ([datetime]::MinValue) 15
    if ($h.Ok) { Complete 0 'already-current' 'release is already current and healthy' }
    Complete 4 'unhealthy-current' ("release is already current but not healthy: " + $h.Why)
}

# ------------------------------------------------------------------------------------------------
# Extract and verify (Install mode)
# ------------------------------------------------------------------------------------------------
$releaseDir = Join-Path $ReleasesDir $release
if ($PSCmdlet.ParameterSetName -eq 'Install') {
    $partial = Join-Path $ReleasesDir ('.partial-{0}-{1}' -f $release, [Guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $partial)
        $expected = @{}
        foreach ($e in $files) { $expected[[string](Get-Prop $e 'path')] = $e }
        $actualFiles = @(Get-ChildItem -LiteralPath $partial -Recurse -Force -File)
        # Same normalization as the FullName of the extracted files (see New-MyriagentHubRelease.ps1: 8.3 short names)
        $prefix = (Get-Item -LiteralPath $partial -Force).FullName.TrimEnd('\') + '\'
        $seen = 0
        foreach ($fi in $actualFiles) {
            if (-not $fi.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "extracted file outside the release folder: $($fi.FullName)" }
            $rel = $fi.FullName.Substring($prefix.Length).Replace('\', '/')
            if (-not $expected.ContainsKey($rel)) { throw "unexpected file in zip: $rel" }
            $e = $expected[$rel]
            if ([int64](Get-Prop $e 'size') -ne $fi.Length) { throw "size mismatch: $rel" }
            if ((Get-FileSha256 $fi.FullName) -ne ([string](Get-Prop $e 'sha256')).ToLowerInvariant()) { throw "sha256 mismatch: $rel" }
            $seen++
        }
        if ($seen -ne $expected.Count) { throw ("zip has {0} files, manifest lists {1}" -f $seen, $expected.Count) }
        if (Test-Path -LiteralPath $releaseDir) { Remove-DirectoryTree $releaseDir }
        [System.IO.Directory]::Move($partial, $releaseDir)
        Copy-Item -LiteralPath $Manifest -Destination (Join-Path $ReleasesDir ($release + '.manifest.json')) -Force
        Write-Log ('extracted and verified {0} files into {1}' -f $seen, $releaseDir)
    }
    catch {
        $msg = $_.Exception.Message
        try { Remove-DirectoryTree $partial } catch { }
        Complete 1 'refused' "extract/verify failed: $msg"
    }
}

# ------------------------------------------------------------------------------------------------
# Admin preflight with the NEW binary (service still running the old one)
# ------------------------------------------------------------------------------------------------
$exe = Join-Path $releaseDir 'MyriagentHub.exe'
$pfOut = [System.IO.Path]::GetTempFileName(); $pfErr = [System.IO.Path]::GetTempFileName()
try {
    $proc = Start-Process -FilePath $exe -ArgumentList @('admin', 'preflight', '--data', $DataRoot) -NoNewWindow -PassThru `
        -RedirectStandardOutput $pfOut -RedirectStandardError $pfErr
    $null = $proc.Handle   # Windows PowerShell 5.1: without this ExitCode stays empty
    if (-not $proc.WaitForExit($PreflightTimeoutSec * 1000)) {
        try { $proc.Kill() } catch { }
        Complete 1 'refused' "admin preflight timed out after $PreflightTimeoutSec s"
    }
    foreach ($l in @(Get-Content -LiteralPath $pfOut) + @(Get-Content -LiteralPath $pfErr)) { if ($l) { Write-Log "preflight: $l" } }
    if ($proc.ExitCode -ne 0) { Complete 1 'refused' "admin preflight failed with exit code $($proc.ExitCode)" }
}
finally { Remove-Item -LiteralPath $pfOut, $pfErr -Force -ErrorAction SilentlyContinue }
Write-Log 'admin preflight passed'

# ------------------------------------------------------------------------------------------------
# Free space for the stopped-state database copy (a full system drive takes the whole machine down)
# ------------------------------------------------------------------------------------------------
$dbBytes = [int64]0
$dbDirForSpace = Join-Path $DataRoot 'db'
if (Test-Path -LiteralPath $dbDirForSpace) {
    foreach ($f in @(Get-ChildItem -LiteralPath $dbDirForSpace -File -Force)) { $dbBytes += $f.Length }
}
$freeBytes = [int64]-1
try { $freeBytes = (New-Object System.IO.DriveInfo ([System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($DataRoot)))).AvailableFreeSpace } catch { }
$needBytes = $dbBytes + ([int64]$MinFreeGB * 1GB)
if ($freeBytes -lt $needBytes) {
    Complete 1 'refused' ("not enough free space for the stopped-state database copy: {0} bytes free, the copy needs {1} bytes and {2} GB must stay free (-MinFreeGB); delete old backups under {3} first" -f $freeBytes, $dbBytes, $MinFreeGB, (Join-Path $DataRoot 'backups'))
}
Write-Log ('free space ok: {0} bytes free, database copy {1} bytes, floor {2} GB' -f $freeBytes, $dbBytes, $MinFreeGB)

# ------------------------------------------------------------------------------------------------
# Drain, stop, stopped-state database copy, switch, start, health gate
# ------------------------------------------------------------------------------------------------

function Invoke-Rollback([string]$Reason) {
    Write-Log "deployment failed: $Reason" 'ERROR'
    Add-History $release $build 'failed' $Reason
    try { Stop-Hub } catch { Write-Log "stop during rollback: $($_.Exception.Message)" 'WARN' }
    try {
        if ($previous) { Set-CurrentRelease $previous } else { Set-CurrentRelease $null }
    }
    catch { Complete 4 'rollback-failed' ("could not restore the junction: " + $_.Exception.Message + " | cause: " + $Reason) }
    if (-not $previous) { Complete 2 'rolled-back' ("first deployment failed; service left stopped | cause: " + $Reason) }
    $since = (Get-Date).ToUniversalTime().AddSeconds(-2)
    try { Start-Hub } catch { Write-Log "start during rollback: $($_.Exception.Message)" 'WARN' }
    $h = Wait-Healthy (Get-ReleaseBuild $previous) (Get-ReleaseVersion $previous) $since $HealthTimeoutSec
    if ($h.Ok) {
        Add-History $previous (Get-ReleaseBuild $previous) 'restored' "rollback from $release"
        Complete 2 'rolled-back' ("previous release $previous is healthy again | cause: " + $Reason)
    }
    Complete 4 'rollback-unhealthy' ("previous release $previous is not healthy after rollback (" + $h.Why + "); the database copy is in backups\predeploy-$release | cause: " + $Reason)
}

$state = Get-HubState
if ($state -eq 'Missing') { Complete 1 'refused' "service $ServiceName does not exist; run tools/hub/Setup-MyriagentHub.ps1" }
Write-Log "service state before stop: $state"

if ($state -eq 'Running') {
    $inbox = Join-Path $DataRoot 'admin-inbox\deploy'
    $drainId = 'drain-{0}-{1:yyyyMMddHHmmss}' -f $release, (Get-Date)
    $drainFile = Join-Path $inbox ($drainId + '.json')
    $drainStep = 'clean'
    try {
        # Every change in the inbox goes through [System.IO.File], never Remove-Item / Move-Item / Rename-Item: a low-privilege account
        # account may not read DataRoot or admin-inbox (only the deploy subfolder is granted), and those Windows
        # PowerShell 5.1 cmdlets read the parent folders while resolving the path, so they fail with "Access is denied"
        # (an UnauthorizedAccessException wrapping a Win32Exception, no target) although that account may change the file.
        # Until 2026-10-23 every drain request was skipped at the rename that way; Publish-DesktopRelease.ps1 already
        # renamed with File.Move in its inbox. Test-MyriagentHubDeploy.ps1 S12c/S12d run the drain with such an ACL.
        # Requests the service never consumed (older than a day) are stale; never let them pile up.
        foreach ($old in @(Get-ChildItem -LiteralPath $inbox -File -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'drain-*' -and $_.LastWriteTimeUtc -lt (Get-Date).ToUniversalTime().AddDays(-1) })) {
            try { [System.IO.File]::Delete($old.FullName) } catch { }
        }
        $drainStep = 'write'
        $req = [ordered]@{ command = 'drain'; id = $drainId; release = $release; retryAfterSec = 30; requestedAt = (Get-Date).ToUniversalTime().ToString('o') }
        $tmp = Join-Path $inbox ($drainId + '.tmp')
        [System.IO.File]::WriteAllText($tmp, (ConvertTo-Json -InputObject $req), $Utf8NoBom)
        $drainStep = 'rename'
        [System.IO.File]::Move($tmp, $drainFile)
        $drainStep = 'wait'
        $ackFile = Join-Path $StatusDir 'drain.json'
        $deadline = (Get-Date).AddSeconds($DrainWaitSec)
        $acked = $false
        while ((Get-Date) -lt $deadline -and -not $acked) {
            if (Test-Path -LiteralPath $ackFile) {
                try { $acked = ([string](Get-Prop (Read-JsonFile $ackFile) 'id') -eq $drainId) } catch { }
            }
            if (-not $acked) { Start-Sleep -Milliseconds 500 }
        }
        if ($acked) { Write-Log 'drain acknowledged' }
        else {
            # Withdraw the request (the admin CLI does the same on a timeout). A request the old service never took would
            # otherwise stay in the inbox, and the NEW release would execute it when it starts (the inbox keeps requests for
            # 10 minutes) and drain right away while the health gate, which does not look at the drain, reports success.
            # The service also ignores deploy drains written before its process started; this keeps the inbox clean.
            $note = 'request withdrawn'
            if ([System.IO.File]::Exists($drainFile)) {
                try { [System.IO.File]::Delete($drainFile) }
                catch { $note = 'request could not be withdrawn (the service is reading it): ' + $_.Exception.GetBaseException().Message }
            }
            else { $note = 'the service took the request but did not answer in time' }
            Write-Log "drain not acknowledged within $DrainWaitSec s; $note; stopping anyway (the service drains on stop)" 'WARN'
        }
    }
    catch {
        # Say which action failed and how: until 2026-10-22 every deploy logged only "Access is denied", which did not tell
        # the file, the call or the exception, while the ACL lets that account write admin-inbox\deploy.
        # Without a drain the devices get no shutdown notice and reconnect into the restart window.
        $err = $_
        $detail = '{0}: {1}' -f $err.Exception.GetType().FullName, $err.Exception.Message
        if ($err.Exception.InnerException) {
            $detail += (' | inner {0}: {1}' -f $err.Exception.InnerException.GetType().FullName, $err.Exception.InnerException.Message)
        }
        $detail += (' | {0} target={1} line {2}' -f $err.FullyQualifiedErrorId, $err.CategoryInfo.TargetName, $err.InvocationInfo.ScriptLineNumber)
        Write-Log "drain request skipped at step ${drainStep}: $detail" 'WARN'
    }
}

try { Stop-Hub } catch {
    $msg = $_.Exception.Message
    try { Start-Hub } catch { }
    # What happens next depends on whether the stop request reached the service (docs/hub-setup.md, exit 4):
    #   Running: the request never reached it (Stop() itself failed, for example access denied), so it keeps serving and
    #     lifts this deployment's drain by itself after Hub:Deploy:DrainMaxSeconds (an administrator's drain from
    #     admin-inbox\ never expires).
    #   StopPending / Stopped: it received the request and timed out while stopping. A stopping service never lifts the
    #     drain, Start-Hub cannot start a service that is still stopping, and nothing else starts it again.
    $after = 'Unknown'
    try { $after = Get-HubState } catch { }
    if ($after -eq 'Running') {
        $next = "service is Running and not stopping: it lifts this deployment's drain by itself after Hub:Deploy:DrainMaxSeconds " +
            "(default 300 s) and /readyz answers 503 until then; 'MyriagentHub.exe admin undrain' lifts it now"
    }
    elseif ($after -eq 'StopPending' -or $after -eq 'Stopped') {
        $next = ("service is {0}: it received the stop request, so it does not lift the drain by itself and nothing starts it " +
            "again; check 'sc query {1}' and run Start-Service {1} once it is Stopped (or -SwitchTo a known good release)") -f $after, $ServiceName
    }
    else {
        $next = "service is ${after}: check 'sc query $ServiceName' and the event log"
    }
    Complete 4 'stop-failed' ("could not stop $ServiceName within $StopTimeoutSec s: $msg | $next")
}
Write-Log 'service stopped'

try {
    $dbDir = Join-Path $DataRoot 'db'
    $dbFiles = @(Get-ChildItem -LiteralPath $dbDir -File -Force -ErrorAction Stop)
    if ($dbFiles.Count -eq 0) { Write-Log 'no database files yet; nothing to copy' }
    else {
        $snapDir = Join-Path $DataRoot ('backups\predeploy-{0}' -f $release)
        if (Test-Path -LiteralPath $snapDir) { $snapDir = '{0}-{1:yyyyMMddHHmmss}' -f $snapDir, (Get-Date) }
        New-Item -ItemType Directory -Path $snapDir -Force | Out-Null
        $rows = @()
        foreach ($f in $dbFiles) {
            $dest = Join-Path $snapDir $f.Name
            Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
            $hs = Get-FileSha256 $f.FullName; $hd = Get-FileSha256 $dest
            if ($hs -ne $hd) { throw "copy verification failed for $($f.Name)" }
            $rows += [ordered]@{ name = $f.Name; size = $f.Length; sha256 = $hd }
        }
        $snapManifest = [ordered]@{ release = $release; previous = [string]$previous; at = (Get-Date).ToUniversalTime().ToString('o'); files = $rows }
        [System.IO.File]::WriteAllText((Join-Path $snapDir 'predeploy.json'), (ConvertTo-Json -InputObject $snapManifest -Depth 4), $Utf8NoBom)
        Write-Log ('database copied while stopped: {0} file(s) -> {1}' -f $rows.Count, $snapDir)
    }
}
catch { Invoke-Rollback ("pre-deploy database copy failed: " + $_.Exception.Message) }

try { Set-CurrentRelease $release } catch { Invoke-Rollback ("junction switch failed: " + $_.Exception.Message) }
Write-Log "current -> $release"

$startedAt = (Get-Date).ToUniversalTime().AddSeconds(-2)
try { Start-Hub } catch { Invoke-Rollback ("service start failed: " + $_.Exception.Message) }
$health = Wait-Healthy $build $version $startedAt $HealthTimeoutSec
if (-not $health.Ok) { Invoke-Rollback ("health gate failed within $HealthTimeoutSec s: " + $health.Why) }
Write-Log 'health gate passed (running.json, selftest.json, healthz)'
Add-History $release $build 'deployed' ''

# Keep the installer that produced a healthy deployment in InstallRoot\tools for manual -SwitchTo rollbacks.
try {
    $toolsDir = Join-Path $InstallRoot 'tools'
    if ((Test-Path -LiteralPath $toolsDir) -and $PSCommandPath -and ($PSCommandPath -ine (Join-Path $toolsDir 'Install-MyriagentHubRelease.ps1'))) {
        Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $toolsDir 'Install-MyriagentHubRelease.ps1') -Force
    }
}
catch { Write-Log "could not refresh tools\Install-MyriagentHubRelease.ps1: $($_.Exception.Message)" 'WARN' }

# ------------------------------------------------------------------------------------------------
# Retention: keep the newest KeepReleases deployed releases (always the current and the previous one)
# ------------------------------------------------------------------------------------------------
try {
    $keep = New-Object System.Collections.Generic.List[string]
    foreach ($r in @($release, $previous)) { if ($r -and -not $keep.Contains($r)) { $keep.Add($r) } }
    foreach ($e in (Get-History)) {
        if ($keep.Count -ge [Math]::Max($KeepReleases, 2)) { break }
        $r = [string](Get-Prop $e 'release')
        if ((Get-Prop $e 'result') -eq 'deployed' -and $r -and -not $keep.Contains($r) -and (Test-Path -LiteralPath (Join-Path $ReleasesDir $r))) { $keep.Add($r) }
    }
    foreach ($d in @(Get-ChildItem -LiteralPath $ReleasesDir -Directory -Force)) {
        if ($keep.Contains($d.Name)) { continue }
        Remove-DirectoryTree $d.FullName
        Remove-Item -LiteralPath (Join-Path $ReleasesDir ($d.Name + '.manifest.json')) -Force -ErrorAction SilentlyContinue
        Write-Log "removed old release $($d.Name)"
    }
    foreach ($mf in @(Get-ChildItem -LiteralPath $ReleasesDir -File -Filter '*.manifest.json' -Force)) {
        if (-not $keep.Contains($mf.Name.Substring(0, $mf.Name.Length - '.manifest.json'.Length))) { Remove-Item -LiteralPath $mf.FullName -Force }
    }
    Write-Log ('kept releases: {0}' -f ($keep -join ', '))
    $backups = Join-Path $DataRoot 'backups'
    $pre = @(Get-ChildItem -LiteralPath $backups -Directory -Filter 'predeploy-*' -Force -ErrorAction SilentlyContinue | Sort-Object CreationTimeUtc -Descending)
    foreach ($old in @($pre | Select-Object -Skip $KeepPredeploy)) {
        Remove-DirectoryTree $old.FullName
        Write-Log "removed old pre-deploy database copy $($old.Name)"
    }
    $logs = @(Get-ChildItem -LiteralPath $logDir -File -Filter 'deploy-*.log' -Force -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending)
    foreach ($old in @($logs | Select-Object -Skip 50)) { Remove-Item -LiteralPath $old.FullName -Force -ErrorAction SilentlyContinue }
}
catch { Write-Log "retention skipped: $($_.Exception.Message)" 'WARN' }

Complete 0 'deployed' ("previous=" + [string]$previous)
