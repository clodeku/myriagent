<#
.SYNOPSIS
    Dry-run test of the deploy tools on a development machine: builds fake releases with New-MyriagentHubRelease.ps1
    and installs them with Install-MyriagentHubRelease.ps1 into a temporary releases/junction structure, with the
    service operations replaced by a fake shim. No administrator rights, no real service, nothing outside WorkDir.
    ASCII only, Windows PowerShell 5.1.

.DESCRIPTION
    Pieces created under WorkDir (deleted at the end unless -Keep):
      fake MyriagentHub.exe   compiled with the .NET Framework csc.exe; "admin preflight" exits with fake-behavior.json's
                          preflightExit
      shim.ps1            Get-HubState / Stop-Hub / Start-Hub: Start-Hub writes status\running.json and
                          status\selftest.json from current\fake-behavior.json, like the real service would
      fake healthz        a TcpListener in a separate powershell.exe answering GET /healthz from the same behavior
                          file; it also plays the service's admin inbox (acknowledges drain requests unless the
                          file fake-inbox-paused exists)
      fake app inbox      another powershell.exe playing the service side of tools/release/Publish-DesktopRelease.ps1
                          (admin-inbox\app-release -> status\app-release.json) in the mode written to
                          fake-appinbox-mode.txt: off, accept, reject, otherrelease or hold (keeps request.json open
                          without delete sharing and never answers, like the service while it verifies)
    Scenarios: first deploy, normal deploy with drain and pre-deploy database copy, unhealthy release with automatic
    rollback, preflight failure, not enough free space for the database copy, zip hash mismatch, tampered manifest, zip content not matching the manifest,
    retention of 3 releases, manual -SwitchTo, already-current re-run, a deploy inbox that does not answer (the
    drain request is withdrawn before the stop), the drain with a low-privilege ACL (this account has no access to
    DataRoot and admin-inbox themselves, only below them: acknowledged, withdrawn, stale requests cleared; S12c/S12d
    set that ACL with icacls and restore the inherited one afterwards), a service that does not accept the stop request (exit 4
    stop-failed; it is still Running, so the message points at the service lifting the deploy drain by itself) and
    one that times out while stopping (exit 4 stop-failed; StopPending, so the message says the drain is not
    lifted and an administrator has to start it).
    Desktop release hand-over (S15-S21, Publish-DesktopRelease.ps1): accepted (exit 0), rejected (exit 1, the three
    files stay), a receipt naming another release (exit 1), only an older receipt (withdrawn, exit 2), no service
    (withdrawn, exit 2), the service holding request.json past the timeout (exit 3; a new run meanwhile exits 1),
    an artifact hash mismatch (exit 1, inbox untouched), leftovers of earlier runs cleared before writing.
    Exit code = number of failed checks.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/deploy/Test-MyriagentHubDeploy.ps1
#>
[CmdletBinding()]
param(
    [string]$WorkDir = '',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# Windows PowerShell 5.1 started from PowerShell 7 (or from a shell that PowerShell 7 started) inherits a PSModulePath
# with PowerShell 7's module folders first; the powershell.exe children below then fail to load Get-FileHash and the
# other Microsoft.PowerShell.Utility commands. Use the Windows PowerShell defaults for this process and its children.
$env:PSModulePath = @(
    (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'WindowsPowerShell\Modules'),
    (Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'),
    (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules')
) -join ';'
$here = $PSScriptRoot
$installer = Join-Path $here 'Install-MyriagentHubRelease.ps1'
$packager = Join-Path $here 'New-MyriagentHubRelease.ps1'
$ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$utf8 = New-Object System.Text.UTF8Encoding($false)
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP ('myriagenthub-deploy-test-{0:yyyyMMdd-HHmmss}' -f (Get-Date)) }
if (Test-Path -LiteralPath $WorkDir) { throw "WorkDir already exists: $WorkDir" }
New-Item -ItemType Directory -Path $WorkDir | Out-Null
$WorkDir = (Resolve-Path -LiteralPath $WorkDir).Path

$installRoot = Join-Path $WorkDir 'Services\MyriagentHub'
$dataRoot = Join-Path $WorkDir 'ProgramData\MyriagentHub'
foreach ($d in @('releases', 'staging', 'tools', 'logs')) { New-Item -ItemType Directory -Path (Join-Path $installRoot $d) -Force | Out-Null }
foreach ($d in @('config', 'db', 'backups', 'status', 'admin-inbox\deploy', 'secrets', 'setup', 'tmp')) { New-Item -ItemType Directory -Path (Join-Path $dataRoot $d) -Force | Out-Null }
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'db\hub.db'), 'fake hub database', $utf8)
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'db\sync.db'), 'fake sync database', $utf8)

$failures = New-Object System.Collections.Generic.List[string]
$passes = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { $script:passes++; Write-Host "  PASS $Message" }
    else { $script:failures.Add($Message); Write-Host "  FAIL $Message" -ForegroundColor Red }
}

# ------------------------------------------------------------------------------------------------
# Fake MyriagentHub.exe
# ------------------------------------------------------------------------------------------------
$fakeSrc = Join-Path $WorkDir 'FakeMyriagentHub.cs'
[System.IO.File]::WriteAllText($fakeSrc, @'
using System;
using System.IO;
using System.Text.RegularExpressions;
static class FakeMyriagentHub
{
    static int Main(string[] args)
    {
        string behavior = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fake-behavior.json");
        int code = 0;
        if (File.Exists(behavior))
        {
            Match m = Regex.Match(File.ReadAllText(behavior), "\"preflightExit\"\\s*:\\s*(-?\\d+)");
            if (m.Success) { code = int.Parse(m.Groups[1].Value); }
        }
        Console.WriteLine("fake MyriagentHub " + string.Join(" ", args) + " -> exit " + code);
        return code;
    }
}
'@, $utf8)
$fakeExe = Join-Path $WorkDir 'FakeMyriagentHub.exe'
& $csc /nologo /target:exe ("/out:" + $fakeExe) $fakeSrc | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $fakeExe)) { throw 'csc failed to build the fake MyriagentHub.exe' }

# ------------------------------------------------------------------------------------------------
# Fake service shim (dot-sourced by Install-MyriagentHubRelease.ps1, so it sees $InstallRoot, $DataRoot, $StatusDir)
# ------------------------------------------------------------------------------------------------
$stateFile = Join-Path $WorkDir 'fake-state.txt'
$callsFile = Join-Path $WorkDir 'fake-calls.log'
[System.IO.File]::WriteAllText($stateFile, 'Stopped', $utf8)
$shim = Join-Path $WorkDir 'shim.ps1'
$shimText = @'
$script:FakeState = '__STATE__'
$script:FakeCalls = '__CALLS__'
function Write-FakeCall([string]$What) { [System.IO.File]::AppendAllText($script:FakeCalls, $What + [Environment]::NewLine) }
function Get-HubState { return ([System.IO.File]::ReadAllText($script:FakeState)).Trim() }
function Stop-Hub {
    Write-FakeCall 'stop'
    $behaviorFile = Join-Path $InstallRoot 'current\fake-behavior.json'
    if (Test-Path -LiteralPath $behaviorFile) {
        $b = [System.IO.File]::ReadAllText($behaviorFile) | ConvertFrom-Json
        if ($b.stopFails) { throw 'fake service did not accept the stop request' }
        if ($b.stopHangs) {
            # The stop request reaches the service, but it does not reach Stopped within the timeout.
            [System.IO.File]::WriteAllText($script:FakeState, 'StopPending')
            throw 'Time out has expired and the operation has not been completed.'
        }
    }
    [System.IO.File]::WriteAllText($script:FakeState, 'Stopped')
}
function Start-Hub {
    Write-FakeCall 'start'
    if ((Get-HubState) -eq 'StopPending') { throw 'fake service cannot start while it is stopping' }
    $behaviorFile = Join-Path $InstallRoot 'current\fake-behavior.json'
    $b = [System.IO.File]::ReadAllText($behaviorFile) | ConvertFrom-Json
    if ($b.startFails) { throw 'fake service failed to start' }
    $enc = New-Object System.Text.UTF8Encoding($false)
    $run = @{ build = $b.runningBuild; version = $b.version; pid = 4242; startedAt = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $StatusDir 'running.json'), $run, $enc)
    $st = @{ ok = [bool]$b.selftestOk; checks = @() } | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $StatusDir 'selftest.json'), $st, $enc)
    [System.IO.File]::WriteAllText($script:FakeState, 'Running')
}
'@
$shimText = $shimText.Replace('__STATE__', $stateFile).Replace('__CALLS__', $callsFile)
[System.IO.File]::WriteAllText($shim, $shimText, $utf8)

# ------------------------------------------------------------------------------------------------
# Fake healthz server and admin inbox (separate process)
# ------------------------------------------------------------------------------------------------
$probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
$probe.Start(); $port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
$serverScript = Join-Path $WorkDir 'fake-healthz.ps1'
$inboxPause = Join-Path $WorkDir 'fake-inbox-paused'
[System.IO.File]::WriteAllText($serverScript, @'
param([int]$Port, [string]$StateFile, [string]$InstallRoot, [string]$DataRoot, [string]$PauseFile)
$enc = New-Object System.Text.UTF8Encoding($false)
$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
while ($true) {
    # Play the service's admin inbox: acknowledge drain requests (unless the test paused the inbox).
    foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $DataRoot 'admin-inbox\deploy') -Filter '*.json' -ErrorAction SilentlyContinue)) {
        if (Test-Path -LiteralPath $PauseFile) { break }
        try {
            $req = [System.IO.File]::ReadAllText($f.FullName) | ConvertFrom-Json
            $ack = @{ id = $req.id; state = 'drained'; at = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json
            [System.IO.File]::WriteAllText((Join-Path $DataRoot 'status\drain.json'), $ack, $enc)
            [System.IO.File]::Delete($f.FullName)   # Remove-Item is denied while S12c/S12d restrict the parent folders
        } catch { }
    }
    if (-not $listener.Pending()) { Start-Sleep -Milliseconds 100; continue }
    $client = $listener.AcceptTcpClient()
    try {
        $stream = $client.GetStream()
        $reader = New-Object System.IO.StreamReader($stream)
        while (($line = $reader.ReadLine()) -ne $null -and $line -ne '') { }
        $state = ([System.IO.File]::ReadAllText($StateFile)).Trim()
        $code = 503; $body = '{"status":"down"}'
        if ($state -eq 'Running') {
            $b = [System.IO.File]::ReadAllText((Join-Path $InstallRoot 'current\fake-behavior.json')) | ConvertFrom-Json
            $code = [int]$b.healthCode
            $body = (@{ status = $b.healthStatus; build = $b.healthBuild } | ConvertTo-Json -Compress)
        }
        $bytes = $enc.GetBytes($body)
        $head = "HTTP/1.1 $code X`r`nContent-Type: application/json`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($head)
        $stream.Write($hb, 0, $hb.Length); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
    } catch { } finally { $client.Close() }
}
'@, $utf8)
$server = Start-Process -FilePath $ps51 -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $serverScript,
    '-Port', $port, '-StateFile', $stateFile, '-InstallRoot', $installRoot, '-DataRoot', $dataRoot, '-PauseFile', $inboxPause) -WindowStyle Hidden -PassThru

# ------------------------------------------------------------------------------------------------
# Fake app-release inbox (separate process, started before S15): the service side of Publish-DesktopRelease.ps1
# ------------------------------------------------------------------------------------------------
$appInboxScript = Join-Path $WorkDir 'fake-appinbox.ps1'
$appModeFile = Join-Path $WorkDir 'fake-appinbox-mode.txt'
[System.IO.File]::WriteAllText($appInboxScript, @'
param([string]$DataRoot, [string]$ModeFile)
$enc = New-Object System.Text.UTF8Encoding($false)
$inbox = Join-Path $DataRoot 'admin-inbox\app-release'
$ack = Join-Path $DataRoot 'status\app-release.json'
$request = Join-Path $inbox 'request.json'
$held = $null
while ($true) {
    $mode = 'off'
    try { $mode = ([System.IO.File]::ReadAllText($ModeFile)).Trim() } catch { }
    if ($held -and $mode -ne 'hold') { $held.Dispose(); $held = $null }
    if ($mode -eq 'off' -or -not [System.IO.File]::Exists($request)) { Start-Sleep -Milliseconds 100; continue }
    if ($mode -eq 'hold') {
        # Like the service while it verifies: request.json stays open without delete sharing, no receipt.
        if (-not $held) { try { $held = New-Object System.IO.FileStream($request, 'Open', 'Read', 'Read') } catch { } }
        Start-Sleep -Milliseconds 100
        continue
    }
    $stream = $null
    try { $stream = New-Object System.IO.FileStream($request, 'Open', 'Read', 'Read') } catch { Start-Sleep -Milliseconds 100; continue }
    try {
        $req = (New-Object System.IO.StreamReader($stream, $enc)).ReadToEnd() | ConvertFrom-Json
        $receipt = [ordered]@{ id = [string]$req.id; ok = $true; release = [string]$req.release; message = 'ingested'; processedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
        if ($mode -eq 'reject') { $receipt.ok = $false; $receipt.message = 'signature' }
        if ($mode -eq 'otherrelease') { $receipt.release = '9.9.9-0000000' }
        [System.IO.File]::WriteAllText($ack + '.tmp', ($receipt | ConvertTo-Json), $enc)
        # Replace like the service's File.Move(overwrite); retried while the script happens to be reading the receipt.
        # [NullString]::Value: a $null argument would reach File.Replace as an empty backup path.
        for ($i = 0; $i -lt 20; $i++) {
            try {
                if ([System.IO.File]::Exists($ack)) { [System.IO.File]::Replace($ack + '.tmp', $ack, [NullString]::Value) } else { [System.IO.File]::Move($ack + '.tmp', $ack) }
                break
            }
            catch { Start-Sleep -Milliseconds 100 }
        }
    }
    catch { }
    finally { $stream.Dispose() }
    # Like the service: request.json goes after the receipt; the three files go only when the release was taken.
    try { [System.IO.File]::Delete($request) } catch { }
    if ($mode -ne 'reject') {
        foreach ($f in @(Get-ChildItem -LiteralPath $inbox -File -Filter 'myriagent-*' -ErrorAction SilentlyContinue)) { try { [System.IO.File]::Delete($f.FullName) } catch { } }
    }
}
'@, $utf8)
$appServer = $null

# ------------------------------------------------------------------------------------------------
# Fake releases
# ------------------------------------------------------------------------------------------------
function New-FakeRelease([string]$Version, [string]$Build, [hashtable]$Behavior) {
    $pub = Join-Path $WorkDir ("publish-$Version-$Build")
    New-Item -ItemType Directory -Path (Join-Path $pub 'wwwroot') -Force | Out-Null
    Copy-Item -LiteralPath $fakeExe -Destination (Join-Path $pub 'MyriagentHub.exe')
    [System.IO.File]::WriteAllText((Join-Path $pub 'wwwroot\index.html'), "<html>$Build</html>", $utf8)
    [System.IO.File]::WriteAllBytes((Join-Path $pub 'MyriagentHub.dll'), [byte[]](1..64 | ForEach-Object { Get-Random -Maximum 256 }))
    $b = @{ version = $Version; runningBuild = $Build; healthBuild = $Build; healthStatus = 'ok'; healthCode = 200; selftestOk = $true; preflightExit = 0; startFails = $false; stopFails = $false; stopHangs = $false }
    foreach ($k in $Behavior.Keys) { $b[$k] = $Behavior[$k] }
    [System.IO.File]::WriteAllText((Join-Path $pub 'fake-behavior.json'), ($b | ConvertTo-Json), $utf8)
    $out = Join-Path $WorkDir ("out-$Version-$Build")
    $ghOut = Join-Path $WorkDir ("gh-output-$Build.txt")
    $env:GITHUB_OUTPUT = $ghOut
    $ErrorActionPreference = 'Continue'
    try {
        & $ps51 -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $packager -PublishDir $pub -OutDir $out -Version $Version -Build $Build `
            -Commit ($Build + '0000000000000000000000000000000000') -DeployTools $installer | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "packager failed for $Build" }
    }
    finally { Remove-Item Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue }
    $o = @{}
    foreach ($l in [System.IO.File]::ReadAllLines($ghOut)) { $i = $l.IndexOf('='); if ($i -gt 0) { $o[$l.Substring(0, $i)] = $l.Substring($i + 1) } }
    $o['zip'] = Join-Path $out $o['zip_name']
    $o['manifest'] = Join-Path $out $o['manifest_name']
    return $o
}

function Invoke-Install([string[]]$Extra) {
    $common = @('-InstallRoot', $installRoot, '-DataRoot', $dataRoot, '-ServiceName', 'MyriagentHubFake', '-ServiceShim', $shim,
        '-HealthUrl', ('http://127.0.0.1:{0}/healthz' -f $port), '-HealthResolve', 'none', '-HealthTimeoutSec', '8', '-DrainWaitSec', '5')
    $ErrorActionPreference = 'Continue'   # keep the child's stderr as text instead of a terminating error
    $output = & $ps51 -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer @($Extra + $common) 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $output | ForEach-Object { Write-Host "    | $_" }
    return @{ Code = $code; Text = ($output -join "`n") }
}

function Get-Current {
    $link = Join-Path $installRoot 'current'
    if (-not (Test-Path -LiteralPath $link)) { return '' }
    $t = (Get-Item -LiteralPath $link -Force).Target
    if ($t -is [array]) { $t = $t[0] }
    return (Split-Path ([string]$t).TrimEnd('\') -Leaf)
}
function Get-RunningBuild { try { return [string](([System.IO.File]::ReadAllText((Join-Path $dataRoot 'status\running.json')) | ConvertFrom-Json).build) } catch { return '' } }
function Get-CallCount([string]$What) { if (-not (Test-Path -LiteralPath $callsFile)) { return 0 }; return @([System.IO.File]::ReadAllLines($callsFile) | Where-Object { $_ -eq $What }).Count }
function Install-Args($R) { return @('-Zip', $R.zip, '-Manifest', $R.manifest, '-ZipSha256', $R.zip_sha256, '-ManifestSha256', $R.manifest_sha256, '-ExpectedBuild', $R.build) }

# A low-privilege account may not read DataRoot or admin-inbox (Setup-MyriagentHub removes their inheritance; such an account is granted
# only subfolders such as admin-inbox\deploy). S12c/S12d give both folders the same shape for this account, without any
# deny entry (SEC-15): inheritance removed and only an inherit-only grant, so the account has full access below them and
# none on the two folders themselves. /reset restores the inherited ACL (the owner may always rewrite it).
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
function Set-RunnerLikeAcl([bool]$On) {
    $ErrorActionPreference = 'Continue'
    foreach ($p in @($dataRoot, (Join-Path $dataRoot 'admin-inbox'))) {
        if ($On) { $out = & $icacls $p /inheritance:r /grant:r ('*{0}:(OI)(CI)(IO)F' -f $me) 2>&1 }
        else { $out = & $icacls $p /reset 2>&1 }
        if ($LASTEXITCODE -ne 0) { throw ('icacls {0} failed: {1}' -f $p, (($out | ForEach-Object { "$_" }) -join ' ')) }
    }
}
function Test-CmdletRenameDenied([string]$Dir) {
    # Positive control: with the ACL in place Move-Item inside $Dir has to fail the way it fails for a low-privilege account,
    # otherwise S12c/S12d would pass without testing anything.
    $p = Join-Path $Dir 'acl-control.tmp'
    [System.IO.File]::WriteAllText($p, 'x', $utf8)
    $denied = $false
    try { Move-Item -LiteralPath $p -Destination (Join-Path $Dir 'acl-control.moved') -ErrorAction Stop }
    catch { $denied = ($_.Exception -is [System.UnauthorizedAccessException]) }
    foreach ($n in 'acl-control.tmp', 'acl-control.moved') { [System.IO.File]::Delete((Join-Path $Dir $n)) }
    return $denied
}

try {
    Write-Host "work dir $WorkDir  fake healthz port $port"
    $A = New-FakeRelease '1.0.0' 'aaaaaaa' @{}
    $B = New-FakeRelease '1.0.1' 'bbbbbbb' @{}
    $C = New-FakeRelease '1.0.2' 'ccccccc' @{ selftestOk = $false }
    $D = New-FakeRelease '1.0.3' 'ddddddd' @{ preflightExit = 3 }
    $E = New-FakeRelease '1.0.4' 'eeeeeee' @{}
    $F = New-FakeRelease '1.0.5' 'fffffff' @{ healthBuild = '0000000' }
    $G = New-FakeRelease '1.0.6' '1111111' @{}
    $H = New-FakeRelease '1.0.7' '2222222' @{ stopFails = $true }
    $I = New-FakeRelease '1.0.8' '3333333' @{}
    $J = New-FakeRelease '1.0.9' '4444444' @{}
    $K = New-FakeRelease '1.0.10' '5555555' @{}
    $L = New-FakeRelease '1.0.11' '6666666' @{}
    $M = New-FakeRelease '1.0.12' '7777777' @{}

    Write-Host 'S0 manifest paths are relative to the publish folder, also when PublishDir is an 8.3 short path'
    $expectedPaths = 'MyriagentHub.dll,MyriagentHub.exe,fake-behavior.json,wwwroot/index.html'
    $pathsOf = {
        param([string]$ManifestPath)
        $names = [string[]]@((([System.IO.File]::ReadAllText($ManifestPath)) | ConvertFrom-Json).files | ForEach-Object { [string]$_.path })
        [Array]::Sort($names, [StringComparer]::Ordinal)
        return ($names -join ',')
    }
    Assert-True ((& $pathsOf $A.manifest) -ceq $expectedPaths) 'S0 manifest of A lists exactly the published files'
    $longPub = Join-Path $WorkDir 'publish-1.0.0-aaaaaaa'
    $shortPub = ''
    try { $shortPub = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($longPub).ShortPath } catch { }
    if ($shortPub -and ($shortPub -ine $longPub)) {
        $shortOut = Join-Path $WorkDir 'out-shortpath'
        $ErrorActionPreference = 'Continue'
        & $ps51 -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $packager -PublishDir $shortPub -OutDir $shortOut -Version '1.0.0' -Build 'aaaaaaa' `
            -Commit 'aaaaaaa0000000000000000000000000000000000' -DeployTools $installer | Out-Host
        Assert-True ($LASTEXITCODE -eq 0) "S0 packager accepts an 8.3 short PublishDir ($shortPub)"
        $shortManifest = Join-Path $shortOut 'myriagenthub-1.0.0-aaaaaaa.manifest.json'
        Assert-True ((Test-Path -LiteralPath $shortManifest) -and ((& $pathsOf $shortManifest) -ceq $expectedPaths)) 'S0 manifest built from the short path lists the same relative paths'
    }
    else { Write-Host '  SKIP S0 short path: no 8.3 name for the work dir on this volume' }

    Write-Host 'S1 first deployment of A'
    $r = Invoke-Install (Install-Args $A)
    Assert-True ($r.Code -eq 0) "S1 exit 0 (got $($r.Code))"
    Assert-True ((Get-Current) -eq $A.release) 'S1 current -> A'
    Assert-True ((Get-RunningBuild) -eq 'aaaaaaa') 'S1 running build A'
    Assert-True (Test-Path -LiteralPath (Join-Path $installRoot ('releases\' + $A.release + '.manifest.json'))) 'S1 manifest kept next to the release'
    Assert-True (Test-Path -LiteralPath (Join-Path $installRoot 'tools\Install-MyriagentHubRelease.ps1')) 'S1 installer copied to InstallRoot\tools for manual rollbacks'

    Write-Host 'S2 normal deployment of B (drain acknowledged, database copied while stopped)'
    $r = Invoke-Install (Install-Args $B)
    Assert-True ($r.Code -eq 0) "S2 exit 0 (got $($r.Code))"
    Assert-True ((Get-Current) -eq $B.release) 'S2 current -> B'
    Assert-True ((Get-RunningBuild) -eq 'bbbbbbb') 'S2 running build B'
    Assert-True ($r.Text -match 'drain acknowledged') 'S2 drain acknowledged by the fake inbox'
    $snap = Join-Path $dataRoot ('backups\predeploy-' + $B.release)
    Assert-True ((Test-Path -LiteralPath (Join-Path $snap 'hub.db')) -and (Test-Path -LiteralPath (Join-Path $snap 'predeploy.json'))) 'S2 predeploy database copy with manifest'

    Write-Host 'S3 unhealthy release C (selftest fails) -> automatic rollback to B'
    $r = Invoke-Install (Install-Args $C)
    Assert-True ($r.Code -eq 2) "S3 exit 2 (got $($r.Code))"
    Assert-True ((Get-Current) -eq $B.release) 'S3 current back to B'
    Assert-True ((Get-RunningBuild) -eq 'bbbbbbb') 'S3 B restarted (running.json rewritten)'
    Assert-True ($r.Text -match 'selftest not passing') 'S3 failure reason reported'

    Write-Host 'S4 preflight failure (D) -> refused, service untouched'
    $stopsBefore = Get-CallCount 'stop'
    $r = Invoke-Install (Install-Args $D)
    Assert-True ($r.Code -eq 1) "S4 exit 1 (got $($r.Code))"
    Assert-True ((Get-CallCount 'stop') -eq $stopsBefore) 'S4 service never stopped'
    Assert-True ((Get-Current) -eq $B.release) 'S4 current still B'

    Write-Host 'S4b not enough free space for the stopped-state database copy (-MinFreeGB above the free space) -> refused, service untouched'
    $stopsBefore = Get-CallCount 'stop'
    $r = Invoke-Install ((Install-Args $E) + @('-MinFreeGB', '100000000'))
    Assert-True ($r.Code -eq 1 -and $r.Text -match 'not enough free space for the stopped-state database copy') "S4b exit 1 with the free space reason (got $($r.Code))"
    Assert-True ((Get-CallCount 'stop') -eq $stopsBefore) 'S4b service never stopped'
    Assert-True ((Get-Current) -eq $B.release) 'S4b current still B'

    Write-Host 'S5 zip hash mismatch -> refused'
    $bad = Install-Args $E
    $bad[5] = ('0' * 64)
    $r = Invoke-Install $bad
    Assert-True ($r.Code -eq 1 -and $r.Text -match 'zip sha256 mismatch') "S5 exit 1 with zip mismatch (got $($r.Code))"

    Write-Host 'S6 tampered manifest -> refused'
    $tampered = Join-Path $WorkDir 'tampered.manifest.json'
    Copy-Item -LiteralPath $E.manifest -Destination $tampered
    [System.IO.File]::AppendAllText($tampered, ' ')
    $args6 = Install-Args $E
    $args6[3] = $tampered
    $r = Invoke-Install $args6
    Assert-True ($r.Code -eq 1 -and $r.Text -match 'manifest sha256 mismatch') "S6 exit 1 with manifest mismatch (got $($r.Code))"

    Write-Host 'S7 zip content does not match the manifest -> refused'
    $evilDir = Join-Path $WorkDir 'evil'
    New-Item -ItemType Directory -Path $evilDir | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($E.zip, $evilDir)
    [System.IO.File]::WriteAllText((Join-Path $evilDir 'extra.txt'), 'not in the manifest', $utf8)
    $evilZip = Join-Path $WorkDir 'evil.zip'
    [System.IO.Compression.ZipFile]::CreateFromDirectory($evilDir, $evilZip)
    $args7 = Install-Args $E
    $args7[1] = $evilZip
    $args7[5] = (Get-FileHash -LiteralPath $evilZip -Algorithm SHA256).Hash
    $r = Invoke-Install $args7
    Assert-True ($r.Code -eq 1) "S7 exit 1 (got $($r.Code))"
    Assert-True ((Get-Current) -eq $B.release) 'S7 current still B'

    Write-Host 'S7b manifest without zipSha256 and a zip with an extra file -> per-file verification refuses'
    $m7 = [System.IO.File]::ReadAllText($E.manifest) | ConvertFrom-Json
    $m7.PSObject.Properties.Remove('zipSha256')
    $m7Path = Join-Path $WorkDir 'nozip.manifest.json'
    [System.IO.File]::WriteAllText($m7Path, ($m7 | ConvertTo-Json -Depth 5), $utf8)
    $args7b = @('-Zip', $evilZip, '-Manifest', $m7Path, '-ZipSha256', (Get-FileHash -LiteralPath $evilZip -Algorithm SHA256).Hash,
        '-ManifestSha256', (Get-FileHash -LiteralPath $m7Path -Algorithm SHA256).Hash, '-ExpectedBuild', 'eeeeeee')
    $r = Invoke-Install $args7b
    Assert-True ($r.Code -eq 1 -and $r.Text -match 'unexpected file in zip: extra.txt') "S7b refused on the extra file (got $($r.Code))"
    Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $installRoot 'releases') -Directory -Force | Where-Object { $_.Name -like '.partial-*' }).Count -eq 0) 'S7b partial extraction removed'

    Write-Host 'S7c manifest with a path outside the release folder -> refused'
    $m7c = [System.IO.File]::ReadAllText($E.manifest) | ConvertFrom-Json
    $m7c.files = @($m7c.files) + @([pscustomobject]@{ path = '../evil.txt'; size = 1; sha256 = ('0' * 64) })
    $m7cPath = Join-Path $WorkDir 'badpath.manifest.json'
    [System.IO.File]::WriteAllText($m7cPath, ($m7c | ConvertTo-Json -Depth 5), $utf8)
    $args7c = @('-Zip', $E.zip, '-Manifest', $m7cPath, '-ZipSha256', $E.zip_sha256,
        '-ManifestSha256', (Get-FileHash -LiteralPath $m7cPath -Algorithm SHA256).Hash, '-ExpectedBuild', 'eeeeeee')
    $r = Invoke-Install $args7c
    Assert-True ($r.Code -eq 1 -and $r.Text -match "bad path '\.\./evil\.txt'") "S7c refused on ../ path (got $($r.Code))"

    Write-Host 'S8 wrong build in healthz (F) -> rollback; then E and G deploy; retention keeps 3'
    $r = Invoke-Install (Install-Args $F)
    Assert-True ($r.Code -eq 2 -and $r.Text -match 'healthz build=0000000') "S8 F rolled back on healthz build mismatch (got $($r.Code))"
    $r = Invoke-Install (Install-Args $E)
    Assert-True ($r.Code -eq 0) "S8 E deployed (got $($r.Code))"
    $r = Invoke-Install (Install-Args $G)
    Assert-True ($r.Code -eq 0) "S8 G deployed (got $($r.Code))"
    $left = @(Get-ChildItem -LiteralPath (Join-Path $installRoot 'releases') -Directory -Force | ForEach-Object { $_.Name } | Sort-Object)
    $want = @($G.release, $E.release, $B.release) | Sort-Object
    Assert-True (($left -join ',') -eq ($want -join ',')) ("S8 releases kept: {0} (want {1})" -f ($left -join ','), ($want -join ','))
    $preCount = @(Get-ChildItem -LiteralPath (Join-Path $dataRoot 'backups') -Directory -Filter 'predeploy-*').Count
    Assert-True ($preCount -le 2) "S8 pre-deploy database copies pruned to 2 (have $preCount)"

    Write-Host 'S9 manual -SwitchTo E'
    $r = Invoke-Install @('-SwitchTo', $E.release)
    Assert-True ($r.Code -eq 0) "S9 exit 0 (got $($r.Code))"
    Assert-True ((Get-Current) -eq $E.release -and (Get-RunningBuild) -eq 'eeeeeee') 'S9 current -> E and running E'

    Write-Host 'S10 re-running the current release -> already-current'
    $r = Invoke-Install @('-SwitchTo', $E.release)
    Assert-True ($r.Code -eq 0 -and $r.Text -match 'already-current') "S10 already-current (got $($r.Code))"

    Write-Host 'S11 the build is exactly the 7-character lower-case commit hash (manifest = running.json = healthz)'
    $ErrorActionPreference = 'Continue'
    $out11 = & $ps51 -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $packager -PublishDir (Join-Path $WorkDir 'publish-1.0.4-eeeeeee') `
        -OutDir (Join-Path $WorkDir 'out-build12') -Version '1.0.4' -Build 'eeeeeee12345' 2>&1 | ForEach-Object { "$_" }
    $code11 = $LASTEXITCODE
    $out11b = & $ps51 -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $packager -PublishDir (Join-Path $WorkDir 'publish-1.0.4-eeeeeee') `
        -OutDir (Join-Path $WorkDir 'out-buildupper') -Version '1.0.4' -Build 'EEEEEEE' 2>&1 | ForEach-Object { "$_" }
    $code11b = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Assert-True ($code11 -ne 0 -and ($out11 -join "`n") -match 'Invalid build') "S11 packager refuses a 12-character build (got $code11)"
    Assert-True ($code11b -ne 0 -and ($out11b -join "`n") -match 'Invalid build') "S11 packager refuses an upper-case build (got $code11b)"
    $m11 = [System.IO.File]::ReadAllText($E.manifest) | ConvertFrom-Json
    $m11.build = 'eeeeeee12345'
    $m11Path = Join-Path $WorkDir 'build12.manifest.json'
    [System.IO.File]::WriteAllText($m11Path, ($m11 | ConvertTo-Json -Depth 5), $utf8)
    $args11 = @('-Zip', $E.zip, '-Manifest', $m11Path, '-ZipSha256', $E.zip_sha256, '-ManifestSha256', (Get-FileHash -LiteralPath $m11Path -Algorithm SHA256).Hash)
    $r = Invoke-Install $args11
    Assert-True ($r.Code -eq 1 -and $r.Text -match "bad build 'eeeeeee12345'") "S11 installer refuses a manifest whose build is not 7 characters (got $($r.Code))"
    Assert-True ((Get-Current) -eq $E.release -and (Get-RunningBuild) -eq 'eeeeeee') 'S11 current and running build unchanged'

    Write-Host 'S12 the deploy inbox does not answer the drain (J) -> the request is withdrawn before the stop, deploy goes on'
    [System.IO.File]::WriteAllText($inboxPause, 'paused', $utf8)
    try {
        $r = Invoke-Install (Install-Args $J)
        $left12 = @(Get-ChildItem -LiteralPath (Join-Path $dataRoot 'admin-inbox\deploy') -File -Force | Where-Object { $_.Name -like 'drain-*' })
    }
    finally { Remove-Item -LiteralPath $inboxPause -Force -ErrorAction SilentlyContinue }
    Assert-True ($r.Code -eq 0 -and (Get-Current) -eq $J.release) "S12 J deployed without a drain answer (got $($r.Code))"
    Assert-True ($r.Text -match 'drain not acknowledged within 5 s; request withdrawn') 'S12 the unanswered drain request is withdrawn'
    Assert-True ($left12.Count -eq 0) ("S12 no drain request left for the new release to execute at start (found {0})" -f (($left12 | ForEach-Object { $_.Name }) -join ', '))

    Write-Host 'S12b the drain request cannot be written (K) -> the log names the step and the exception, deploy goes on'
    $deployInbox = Join-Path $dataRoot 'admin-inbox\deploy'
    $deployInboxAway = Join-Path $dataRoot 'admin-inbox\deploy.away'
    Rename-Item -LiteralPath $deployInbox -NewName 'deploy.away'
    try { $r = Invoke-Install (Install-Args $K) }
    finally { Rename-Item -LiteralPath $deployInboxAway -NewName 'deploy' }
    Assert-True ($r.Code -eq 0 -and (Get-Current) -eq $K.release) "S12b K deployed without a drain (got $($r.Code))"
    Assert-True ($r.Text -match 'drain request skipped at step write: \S+: .+ \| inner System\.IO\.DirectoryNotFoundException: .+ \| \S+ target=.* line \d+') 'S12b the skipped drain names the step, the exception types and the line'

    Write-Host 'S12c the runner may not read DataRoot or admin-inbox, like a low-privilege account (L) -> drain acknowledged, stale requests cleared'
    $stale = Join-Path $deployInbox 'drain-0.9.0-0000000-20261001000000.tmp'
    [System.IO.File]::WriteAllText($stale, '{}', $utf8)
    [System.IO.File]::SetLastWriteTimeUtc($stale, (Get-Date).ToUniversalTime().AddDays(-2))
    Set-RunnerLikeAcl $true
    try {
        $control = Test-CmdletRenameDenied $deployInbox
        $r = Invoke-Install (Install-Args $L)
    }
    finally { Set-RunnerLikeAcl $false }
    Assert-True $control 'S12c control: with this ACL Move-Item inside the deploy inbox is denied for a low-privilege account'
    Assert-True ($r.Code -eq 0 -and (Get-Current) -eq $L.release) "S12c L deployed (got $($r.Code))"
    Assert-True ($r.Text -match 'drain acknowledged') 'S12c drain acknowledged although the runner cannot read the parent folders'
    Assert-True ($r.Text -notmatch 'drain request skipped') 'S12c no drain step skipped'
    Assert-True (-not (Test-Path -LiteralPath $stale)) 'S12c a drain request file older than a day is cleared'

    Write-Host 'S12d same ACL and the deploy inbox does not answer (M) -> the request is withdrawn'
    [System.IO.File]::WriteAllText($inboxPause, 'paused', $utf8)
    Set-RunnerLikeAcl $true
    try {
        $r = Invoke-Install (Install-Args $M)
        $left12d = @(Get-ChildItem -LiteralPath $deployInbox -File -Force | Where-Object { $_.Name -like 'drain-*' })
    }
    finally {
        Set-RunnerLikeAcl $false
        Remove-Item -LiteralPath $inboxPause -Force -ErrorAction SilentlyContinue
    }
    Assert-True ($r.Code -eq 0 -and (Get-Current) -eq $M.release) "S12d M deployed without a drain answer (got $($r.Code))"
    Assert-True ($r.Text -match 'drain not acknowledged within 5 s; request withdrawn') 'S12d the unanswered drain request is withdrawn'
    Assert-True ($left12d.Count -eq 0) ("S12d no drain request left (found {0})" -f (($left12d | ForEach-Object { $_.Name }) -join ', '))

    Write-Host 'S13 the service does not stop (H is current) -> stop-failed, exit 4, the service keeps running'
    $r = Invoke-Install (Install-Args $H)
    Assert-True ($r.Code -eq 0) "S13 H deployed (got $($r.Code))"
    $r = Invoke-Install (Install-Args $I)
    Assert-True ($r.Code -eq 4 -and $r.Text -match 'stop-failed') "S13 exit 4 stop-failed when the service does not stop (got $($r.Code))"
    Assert-True ($r.Text -match 'service is Running and not stopping: it lifts this deployment.s drain by itself after Hub:Deploy:DrainMaxSeconds') 'S13 message: the service is Running and lifts the deploy drain by itself after Hub:Deploy:DrainMaxSeconds'
    Assert-True ((Get-Current) -eq $H.release -and ([System.IO.File]::ReadAllText($stateFile)).Trim() -eq 'Running') 'S13 current still H and the service still running'

    Write-Host 'S14 the stop request reaches H but it does not finish stopping (StopPending) -> stop-failed, no automatic undrain'
    $hBehavior = Join-Path $installRoot 'current\fake-behavior.json'
    $hb = [System.IO.File]::ReadAllText($hBehavior) | ConvertFrom-Json
    $hb.stopFails = $false
    $hb.stopHangs = $true
    [System.IO.File]::WriteAllText($hBehavior, ($hb | ConvertTo-Json), $utf8)
    $startsBefore = Get-CallCount 'start'
    $r = Invoke-Install (Install-Args $I)
    Assert-True ($r.Code -eq 4 -and $r.Text -match 'stop-failed') "S14 exit 4 stop-failed when the service times out while stopping (got $($r.Code))"
    Assert-True ($r.Text -match 'service is StopPending: it received the stop request, so it does not lift the drain by itself and nothing starts it again') 'S14 message: StopPending, the drain is not lifted and nothing starts the service again'
    Assert-True ($r.Text -match 'Start-Service MyriagentHubFake once it is Stopped') 'S14 message tells the administrator to start the service once it is Stopped'
    Assert-True ($r.Text -notmatch 'lifts this deployment.s drain by itself') 'S14 message does not promise the automatic undrain'
    Assert-True ((Get-CallCount 'start') -eq $startsBefore + 1 -and ([System.IO.File]::ReadAllText($stateFile)).Trim() -eq 'StopPending' -and (Get-Current) -eq $H.release) 'S14 the script tried to start it again, the service is still StopPending and current is still H'

    $hist = [System.IO.File]::ReadAllText((Join-Path $installRoot 'deploy-history.json')) | ConvertFrom-Json
    Assert-True (@($hist).Count -ge 8) ('history has {0} entries' -f @($hist).Count)

    # --------------------------------------------------------------------------------------------
    # Desktop release hand-over: tools/release/Publish-DesktopRelease.ps1 against the fake app inbox
    # --------------------------------------------------------------------------------------------
    $publisher = Join-Path (Split-Path $here -Parent) 'release\Publish-DesktopRelease.ps1'
    $appInbox = Join-Path $dataRoot 'admin-inbox\app-release'
    $appAck = Join-Path $dataRoot 'status\app-release.json'
    $appRequest = Join-Path $appInbox 'request.json'
    New-Item -ItemType Directory -Path $appInbox -Force | Out-Null
    $artifact = Join-Path $WorkDir 'desktop-artifact'
    New-Item -ItemType Directory -Path $artifact | Out-Null
    $rel = '0.2.300-ab12cd3'
    $appFiles = @(('myriagent-{0}.zip' -f $rel), ('myriagent-{0}.manifest.json' -f $rel), ('myriagent-{0}.manifest.sig' -f $rel))
    [System.IO.File]::WriteAllBytes((Join-Path $artifact $appFiles[0]), [byte[]](1..4096 | ForEach-Object { Get-Random -Maximum 256 }))
    [System.IO.File]::WriteAllText((Join-Path $artifact $appFiles[1]), ('{"schema":1,"release":"' + $rel + '"}'), $utf8)
    [System.IO.File]::WriteAllText((Join-Path $artifact $appFiles[2]), '{"kid":"0123456789abcdef","sig":"AA"}', $utf8)
    $appHash = @($appFiles | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $artifact $_) -Algorithm SHA256).Hash.ToLowerInvariant() })
    [System.IO.File]::WriteAllText($appModeFile, 'off', $utf8)
    $appServer = Start-Process -FilePath $ps51 -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $appInboxScript,
        '-DataRoot', $dataRoot, '-ModeFile', $appModeFile) -WindowStyle Hidden -PassThru
    function Set-AppInbox([string]$Mode) {
        [System.IO.File]::WriteAllText($appModeFile, $Mode, $utf8)
        Start-Sleep -Milliseconds 400   # the fake polls every 100 ms
    }
    function Get-InboxNames { return (@(Get-ChildItem -LiteralPath $appInbox -File -Force | ForEach-Object { $_.Name } | Sort-Object) -join ',') }
    function Invoke-Publish([string]$Id, [int]$TimeoutSec = 15, [string]$ZipSha256 = '') {
        if (-not $ZipSha256) { $ZipSha256 = $appHash[0] }
        $publishArgs = @('-ArtifactDir', $artifact, '-Release', $rel, '-ZipSha256', $ZipSha256, '-ManifestSha256', $appHash[1], '-SigSha256', $appHash[2],
            '-DataRoot', $dataRoot, '-RequestId', $Id, '-TimeoutSec', $TimeoutSec)
        $ErrorActionPreference = 'Continue'
        $output = & $ps51 -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $publisher @publishArgs 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
        $output | ForEach-Object { Write-Host "    | $_" }
        return @{ Code = $code; Text = ($output -join "`n") }
    }

    Write-Host 'S15 the service ingests the release -> exit 0 with its receipt'
    Set-AppInbox 'accept'
    $r = Invoke-Publish 'pub-s15'
    Assert-True ($r.Code -eq 0) "S15 exit 0 (got $($r.Code))"
    Assert-True ($r.Text -match 'RECEIPT \{"id":"pub-s15","ok":true,"release":"0\.2\.300-ab12cd3","message":"ingested"') 'S15 RECEIPT line carries the receipt of this request'

    Write-Host 'S16 the service rejects the release (ok:false) -> exit 1; the three files stay, request.json is gone'
    Set-AppInbox 'reject'
    $r = Invoke-Publish 'pub-s16'
    Assert-True ($r.Code -eq 1 -and $r.Text -match '"message":"signature"') "S16 exit 1 with the rejection code (got $($r.Code))"
    Start-Sleep -Milliseconds 500   # the fake deletes request.json right after the receipt
    Assert-True ((Get-InboxNames) -eq (@($appFiles | Sort-Object) -join ',')) ('S16 the three files stay for inspection, request.json is gone: {0}' -f (Get-InboxNames))

    Write-Host 'S16b a receipt for this id that names another release -> exit 1'
    Set-AppInbox 'otherrelease'
    $r = Invoke-Publish 'pub-s16b'
    Assert-True ($r.Code -eq 1 -and $r.Text -match 'names another release') "S16b exit 1 (got $($r.Code))"

    Write-Host 'S17 only an older receipt with another id -> ignored, the request is withdrawn on timeout, exit 2'
    Set-AppInbox 'off'
    [System.IO.File]::WriteAllText($appAck, '{"id":"pub-older","ok":true,"release":"0.2.300-ab12cd3","message":"ingested","processedAt":1}', $utf8)
    $r = Invoke-Publish 'pub-s17' 3
    Assert-True ($r.Code -eq 2) "S17 exit 2 (got $($r.Code))"
    Assert-True ($r.Text -cnotmatch 'RECEIPT ') 'S17 the receipt of another request is not taken for this one'
    Assert-True (-not (Test-Path -LiteralPath $appRequest)) 'S17 request.json withdrawn'

    Write-Host 'S18 no service and no receipt -> the request is withdrawn on timeout, exit 2'
    Remove-Item -LiteralPath $appAck -Force
    $r = Invoke-Publish 'pub-s18' 3
    Assert-True ($r.Code -eq 2 -and $r.Text -match 'request\.json withdrawn') "S18 exit 2 (got $($r.Code))"
    Assert-True (@(Get-ChildItem -LiteralPath $appInbox -Force -Filter 'request.json*').Count -eq 0) 'S18 request.json deleted (nothing named request.json* left)'

    Write-Host 'S19 the service holds request.json past the timeout -> the withdrawal fails, exit 3; a new run meanwhile exits 1'
    Set-AppInbox 'hold'
    $r = Invoke-Publish 'pub-s19' 3
    Assert-True ($r.Code -eq 3 -and $r.Text -match 'had already taken the request') "S19 exit 3 (got $($r.Code))"
    Assert-True (Test-Path -LiteralPath $appRequest) 'S19 request.json is still there (the service has it)'
    $r = Invoke-Publish 'pub-s19b' 3
    Assert-True ($r.Code -eq 1 -and $r.Text -match 'still being processed') "S19 a new run while the service holds the earlier request refuses, exit 1 (got $($r.Code))"
    Assert-True (Test-Path -LiteralPath $appRequest) 'S19 the refused run left the earlier request alone'
    Set-AppInbox 'off'

    Write-Host 'S20 artifact hash mismatch -> exit 1, the inbox is not touched'
    $before = Get-InboxNames
    $r = Invoke-Publish 'pub-s20' 3 ('0' * 64)
    Assert-True ($r.Code -eq 1 -and $r.Text -match 'zip sha256 mismatch') "S20 exit 1 on the hash mismatch (got $($r.Code))"
    Assert-True ((Get-InboxNames) -eq $before) ('S20 nothing written into or removed from the inbox: {0}' -f (Get-InboxNames))

    Write-Host 'S21 leftovers of earlier runs are cleared before the new request is written'
    foreach ($n in @('request.json.tmp', 'request.json.retracted-0123', 'myriagent-0.2.299-1111111.zip', 'myriagent-0.2.299-1111111.manifest.json',
            'myriagent-0.2.299-1111111.manifest.sig', 'notes.txt')) {
        [System.IO.File]::WriteAllText((Join-Path $appInbox $n), 'left over', $utf8)
    }
    Assert-True (Test-Path -LiteralPath $appRequest) 'S21 request.json of S19 is still in the inbox (not taken by the service)'
    $r = Invoke-Publish 'pub-s21' 2
    Assert-True ($r.Code -eq 2) "S21 exit 2 (no service; got $($r.Code))"
    Assert-True ($r.Text -match 'withdrew a request\.json left by an earlier run') 'S21 the stale request.json is withdrawn first'
    Assert-True ((Get-InboxNames) -eq (@($appFiles + 'notes.txt' | Sort-Object) -join ',')) ('S21 only the three new files and the unrelated notes.txt are left: {0}' -f (Get-InboxNames))
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    if ($appServer -and -not $appServer.HasExited) { Stop-Process -Id $appServer.Id -Force -ErrorAction SilentlyContinue }
    try { Set-RunnerLikeAcl $false } catch { Write-Host "WARN could not restore the ACL of S12c/S12d: $($_.Exception.Message)" }
    if (-not $Keep) {
        $link = Join-Path $installRoot 'current'
        if (Test-Path -LiteralPath $link) { & (Join-Path $env:SystemRoot 'System32\cmd.exe') /c rmdir $link | Out-Null }
        & (Join-Path $env:SystemRoot 'System32\cmd.exe') /c rmdir /s /q $WorkDir | Out-Null
    }
}

Write-Host ''
Write-Host ('{0} passed, {1} failed' -f $passes, $failures.Count)
foreach ($f in $failures) { Write-Host "  FAILED: $f" }
exit $failures.Count
