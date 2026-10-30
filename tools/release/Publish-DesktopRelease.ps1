<#
.SYNOPSIS
    Hands one desktop release (zip, manifest, signature) to the MyriagentHub app-release inbox on this computer (the hub)
    and waits for the service's receipt. ASCII only, Windows PowerShell 5.1.

.DESCRIPTION
    Run as Administrator on the hub computer (tools/release/New-DesktopRelease.ps1 -Submit calls it); any account that can
    write DataRoot\admin-inbox\app-release and read DataRoot\status works.
    The service copies the three files and verifies everything again on its own copy (signature against the
    release keys built into MyriagentHub, zip hash and entries, version, protocol); this script only makes sure that
    it hands over exactly what New-DesktopRelease.ps1 produced.

      1. Checks the parameters and the SHA-256 of the three files against New-DesktopRelease.ps1's outputs (.NET SHA256:
         Get-FileHash is missing when Windows PowerShell inherits a PowerShell 7 PSModulePath). Any problem exits 1
         and leaves the inbox untouched.
      2. Clears what an earlier run left in the inbox: request.json (withdrawn the same way as in step 5),
         request.json.tmp, request.json.retracted-* and myriagent-*.zip / *.manifest.json / *.manifest.sig.
         Other files are left alone. A request.json the service still holds means that an earlier request is
         being processed: exit 1, nothing is removed.
      3. Copies the three files into the inbox, writes request.json.tmp {"id","release","requestedAt"} (ASCII, no
         BOM; the service does not read requestedAt) and renames it to request.json, last: the service only acts
         on request.json.
      4. Reads DataRoot\status\app-release.json once a second (UTF-8, ConvertFrom-Json; a read or parse failure
         means no receipt yet). A receipt with another id is an older one and is ignored.
      5. Without a receipt within TimeoutSec it withdraws the request by renaming request.json aside and deleting
         the renamed file. The service keeps request.json open without delete sharing while it works on it, so the
         rename fails exactly when the service has taken the request (a plain delete cannot tell "deleted" from
         "already gone": .NET treats a missing file as deleted). The receipt is read once more either way: the
         service writes it before it closes and deletes request.json.

    Every run ends with a RESULT line: copies, writes and deletes use [System.IO.File] inside try/catch, so a
    failure is exit 1 (whatever was half written is cleared by the next run), never an unhandled error.

    Exit codes:
      0  published: a receipt for this id with ok:true and this release (message ingested, or already_ingested
         when the same manifest is handed over again)
      1  not published: bad parameters, missing file, hash mismatch, inbox missing, an earlier request still being
         processed, or the service rejected it (receipt ok:false: the reason code is in message, the details are
         in the service log diag\myriagenthub-*.log; the three files stay in the inbox until the next run)
      2  no receipt within TimeoutSec and the request was withdrawn: the service did not take it and never will
      3  no receipt within TimeoutSec and the service had already taken the request: the result is unknown; check
         the releases page (an ingested release is also in the audit log as app.release.ingested)

    Contract with the service (docs/release.md; AppReleaseInterfaceTests guards the two sides):
      DataRoot\admin-inbox\app-release\request.json     {"id":"<id>","release":"<release>","requestedAt":"<ISO 8601 UTC>"}
                                                          written last, next to the three files
      DataRoot\status\app-release.json                  {"id","ok","release","message","processedAt"}

    -WhatIf checks the parameters, the files and their hashes, prints the plan (PLAN lines, including the request
    it would write) and writes nothing.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Publish-DesktopRelease.ps1 -ArtifactDir <dir> -Release 0.2.300-ab12cd3 -ZipSha256 <hex> -ManifestSha256 <hex> -SigSha256 <hex>
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactDir,
    [Parameter(Mandatory = $true)][string]$Release,
    [Parameter(Mandatory = $true)][string]$ZipSha256,
    [Parameter(Mandatory = $true)][string]$ManifestSha256,
    [Parameter(Mandatory = $true)][string]$SigSha256,
    [string]$RequestId = ('pub-' + [guid]::NewGuid().ToString('N')),
    [string]$DataRoot = 'C:\ProgramData\MyriagentHub',
    [int]$TimeoutSec = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$dryRun = [bool]$WhatIfPreference
# Every write below is gated by $dryRun; with WhatIf off, cmdlet auto-loading prints no "What if" noise.
$WhatIfPreference = $false
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Interface with the MyriagentHub service (AppReleaseInterfaceTests compares these three lines with HubPaths).
$InboxRelative = 'admin-inbox\app-release'
$ReceiptRelative = 'status\app-release.json'
$RequestName = 'request.json'

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    Write-Host ('[{0:yyyy-MM-dd HH:mm:ss}] {1,-5} {2}' -f (Get-Date), $Level, $Message)
}

function Get-Prop($Object, [string]$Name) {
    # StrictMode-safe property read; returns $null when the property does not exist.
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Get-Sha256Hex([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try { $hash = $sha.ComputeHash($stream) }
    finally { $stream.Dispose(); $sha.Dispose() }
    return ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
}

function Complete([int]$Code, [string]$Result, [string]$Detail) {
    $level = 'INFO'
    if ($Code -ne 0) { $level = 'ERROR' }
    Write-Log ('{0} (exit {1}): {2}' -f $Result, $Code, $Detail) $level
    $summary = [ordered]@{ result = $Result; exitCode = $Code; release = $Release; id = $RequestId; detail = $Detail }
    Write-Host ('RESULT {0}' -f (ConvertTo-Json -InputObject $summary -Compress))
    if ($env:GITHUB_STEP_SUMMARY -and -not $dryRun) {
        $line = 'Desktop release `{0}` (request `{1}`): **{2}**, exit {3}. {4}' -f $Release, $RequestId, $Result, $Code, ($Detail -replace '[^\x20-\x7E]', '?')
        try { [System.IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $line + "`n", $Utf8NoBom) } catch { }
    }
    exit $Code
}

function Undo-Request([string]$Path) {
    # Returns withdrawn (moved aside by this script: the service never sees it), in-use (the service holds it) or gone.
    $aside = '{0}.retracted-{1}' -f $Path, [guid]::NewGuid().ToString('N')
    try { [System.IO.File]::Move($Path, $aside) }
    catch {
        if (-not [System.IO.File]::Exists($Path)) { return 'gone' }
        Write-Log ('{0} cannot be moved aside: {1}' -f $Path, $_.Exception.GetBaseException().Message)
        return 'in-use'
    }
    try { [System.IO.File]::Delete($aside) }
    catch { Write-Log ('could not delete {0}; the next run removes it' -f $aside) 'WARN' }
    return 'withdrawn'
}

function Read-Receipt {
    # The service replaces the receipt atomically; a read or parse failure means no receipt yet.
    try {
        if (-not [System.IO.File]::Exists($receiptPath)) { return $null }
        $receipt = [System.IO.File]::ReadAllText($receiptPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    }
    catch { return $null }
    if ($null -eq $receipt -or $receipt -is [array]) { return $null }
    if ([string](Get-Prop $receipt 'id') -cne $RequestId) { return $null }   # an older receipt
    return $receipt
}

function Complete-WithReceipt($receipt) {
    Write-Host ('RECEIPT {0}' -f (ConvertTo-Json -InputObject $receipt -Compress))
    $ok = (Get-Prop $receipt 'ok')
    $message = [string](Get-Prop $receipt 'message')
    $receiptRelease = [string](Get-Prop $receipt 'release')
    if ($ok -is [bool] -and $ok -and $receiptRelease -ceq $Release) { Complete 0 'published' ('the service accepted {0} ({1})' -f $Release, $message) }
    if ($ok -is [bool] -and $ok) { Complete 1 'rejected' ('the receipt for this request names another release ({0})' -f $receiptRelease) }
    Complete 1 'rejected' ('the service rejected {0} ({1}); the three files stay in the inbox for inspection, the details are in the service log (diag\myriagenthub-*.log)' -f $Release, $message)
}

# ------------------------------------------------------------------------------------------------
# 1. Parameters and hashes (nothing is written before these pass)
# ------------------------------------------------------------------------------------------------
Write-Log ('Publish-DesktopRelease release={0} id={1} dataRoot={2} timeout={3}s dryRun={4}' -f $Release, $RequestId, $DataRoot, $TimeoutSec, $dryRun)
if ($Release -cnotmatch '^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})-[0-9a-f]{7}\z') {
    Complete 1 'refused' ('invalid release {0}: expected <major>.<minor>.<patch>-<7 lower-case hex>' -f $Release)
}
if ($RequestId -cnotmatch '^[A-Za-z0-9._-]{1,128}\z' -or $RequestId.Contains('..')) {
    Complete 1 'refused' ('invalid request id {0}: letters, digits and -._ only, at most 128, no ..' -f $RequestId)
}
foreach ($pair in @(@('zip', $ZipSha256), @('manifest', $ManifestSha256), @('signature', $SigSha256))) {
    if ($pair[1] -notmatch '^[0-9a-fA-F]{64}\z') { Complete 1 'refused' ('{0} sha256 must be 64 hex characters' -f $pair[0]) }
}
if ($TimeoutSec -lt 1 -or $TimeoutSec -gt 3600) { Complete 1 'refused' ('TimeoutSec must be 1..3600 (got {0})' -f $TimeoutSec) }
$rooted = $false
try { $rooted = [System.IO.Path]::IsPathRooted($DataRoot) } catch { }   # .NET Framework throws on invalid path characters
if (-not $rooted) { Complete 1 'refused' ('DataRoot must be an absolute path: {0}' -f $DataRoot) }
if (-not (Test-Path -LiteralPath $ArtifactDir -PathType Container)) { Complete 1 'refused' ('artifact folder not found: {0}' -f $ArtifactDir) }
try { $ArtifactDir = (Get-Item -LiteralPath $ArtifactDir -Force).FullName }
catch { Complete 1 'refused' ('cannot open the artifact folder {0}: {1}' -f $ArtifactDir, $_.Exception.GetBaseException().Message) }

$files = @(
    [pscustomobject]@{ Kind = 'zip'; Name = ('myriagent-{0}.zip' -f $Release); Expected = $ZipSha256.ToLowerInvariant() },
    [pscustomobject]@{ Kind = 'manifest'; Name = ('myriagent-{0}.manifest.json' -f $Release); Expected = $ManifestSha256.ToLowerInvariant() },
    [pscustomobject]@{ Kind = 'signature'; Name = ('myriagent-{0}.manifest.sig' -f $Release); Expected = $SigSha256.ToLowerInvariant() }
)
foreach ($f in $files) {
    $source = Join-Path $ArtifactDir $f.Name
    if (-not [System.IO.File]::Exists($source)) { Complete 1 'refused' ('missing {0} file: {1}' -f $f.Kind, $source) }
    $actual = ''
    try { $actual = Get-Sha256Hex $source }
    catch { Complete 1 'refused' ('cannot read the {0} file {1}: {2}' -f $f.Kind, $source, $_.Exception.GetBaseException().Message) }
    if ($actual -cne $f.Expected) { Complete 1 'refused' ('{0} sha256 mismatch: expected {1}, got {2} ({3})' -f $f.Kind, $f.Expected, $actual, $f.Name) }
    Write-Log ('{0} sha256 ok {1}' -f $f.Name, $actual)
}

$inbox = Join-Path $DataRoot $InboxRelative
$requestPath = Join-Path $inbox $RequestName
$receiptPath = Join-Path $DataRoot $ReceiptRelative
if (-not [System.IO.Directory]::Exists($inbox)) {
    Complete 1 'refused' ('inbox {0} is missing or this account cannot see it: start MyriagentHub once (the service creates the inbox at startup) and run this script as Administrator' -f $inbox)
}
$requestedAt = [DateTime]::UtcNow.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
$requestJson = ConvertTo-Json -InputObject ([ordered]@{ id = $RequestId; release = $Release; requestedAt = $requestedAt }) -Compress
$leftovers = @('request.json.tmp', 'request.json.retracted-*', 'myriagent-*.zip', 'myriagent-*.manifest.json', 'myriagent-*.manifest.sig')

if ($dryRun) {
    Write-Host ('PLAN clean {0}: withdraw {1} if present; delete {2}' -f $inbox, $RequestName, ($leftovers -join ', '))
    foreach ($f in $files) { Write-Host ('PLAN copy {0} -> {1}' -f (Join-Path $ArtifactDir $f.Name), (Join-Path $inbox $f.Name)) }
    Write-Host ('PLAN request {0} {1}' -f $requestPath, $requestJson)
    Write-Host ('PLAN wait {0} for id {1}, up to {2} s' -f $receiptPath, $RequestId, $TimeoutSec)
    Complete 0 'dry-run' 'parameters, files and hashes ok; nothing written'
}

# ------------------------------------------------------------------------------------------------
# 2. Clear what an earlier run left behind
# ------------------------------------------------------------------------------------------------
$stale = Undo-Request $requestPath
if ($stale -eq 'in-use') {
    Complete 1 'refused' ('an earlier request is still being processed (the service holds {0}); wait for its receipt in {1} and run the publish job again' -f $requestPath, $receiptPath)
}
if ($stale -eq 'withdrawn') { Write-Log 'withdrew a request.json left by an earlier run (the service had not taken it)' 'WARN' }
$present = @()
try { $present = @(Get-ChildItem -LiteralPath $inbox -File -Force) }
catch { Complete 1 'refused' ('cannot list the inbox {0}: {1}' -f $inbox, $_.Exception.GetBaseException().Message) }
foreach ($item in $present) {
    foreach ($pattern in $leftovers) {
        if ($item.Name -like $pattern) {
            try { [System.IO.File]::Delete($item.FullName) }
            catch { Complete 1 'refused' ('cannot remove the leftover {0}: {1}' -f $item.FullName, $_.Exception.GetBaseException().Message) }
            Write-Log ('removed leftover {0}' -f $item.Name)
            break
        }
    }
}

# ------------------------------------------------------------------------------------------------
# 3. Hand over: the three files, then request.json (atomic rename)
# ------------------------------------------------------------------------------------------------
try {
    foreach ($f in $files) { [System.IO.File]::Copy((Join-Path $ArtifactDir $f.Name), (Join-Path $inbox $f.Name), $false) }
    $tmp = Join-Path $inbox ($RequestName + '.tmp')
    [System.IO.File]::WriteAllText($tmp, $requestJson, [System.Text.Encoding]::ASCII)
    [System.IO.File]::Move($tmp, $requestPath)
}
catch {
    Complete 1 'refused' ('could not write into the inbox {0}: {1} (the next run clears what was written)' -f $inbox, $_.Exception.GetBaseException().Message)
}
Write-Log ('request written: {0}' -f $requestJson)

# ------------------------------------------------------------------------------------------------
# 4. Wait for the receipt
# ------------------------------------------------------------------------------------------------
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
do {
    $receipt = Read-Receipt
    if ($null -ne $receipt) { Complete-WithReceipt $receipt }
    Start-Sleep -Seconds 1
} while ([DateTime]::UtcNow -lt $deadline)

# ------------------------------------------------------------------------------------------------
# 5. Timeout: withdraw the request unless the service has taken it
# ------------------------------------------------------------------------------------------------
$taken = Undo-Request $requestPath
$receipt = Read-Receipt
if ($null -ne $receipt) { Complete-WithReceipt $receipt }
if ($taken -eq 'withdrawn') {
    Complete 2 'withdrawn' ('no receipt within {0} s; request.json withdrawn: the service did not take the request and will not process it (the three files stay in the inbox until the next run)' -f $TimeoutSec)
}
Complete 3 'unknown' ('no receipt within {0} s and the service had already taken the request ({1}): the result is unknown; check the releases page (an ingested release is also in the audit log as app.release.ingested)' -f $TimeoutSec, $taken)
