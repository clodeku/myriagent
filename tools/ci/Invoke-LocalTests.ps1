<#
.SYNOPSIS
    Runs the whole unit test suite (Myriagent.Tests and MyriagentHub.Tests) on this machine as several concurrent
    test processes balanced by the previous run's timings. Same tests as `dotnet test Myriagent.slnx -c Release`,
    each exactly once. ASCII only; runs on Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
    Why (measured 2026-11-10 on the dev machine): the plain `dotnet test Myriagent.slnx` took about 25 minutes.
    - xUnit v2 runs the DisableParallelization collections one after another, after everything else (about 17 of the
      25 minutes in Myriagent.Tests). They guard process-wide state (AppPaths, DLP patterns, memory measurement), so
      separate processes can run them at the same time. Their classes are found in the sources ([Collection(X.Name)]
      where X has [CollectionDefinition(..., DisableParallelization = true)]) and spread over -SerialShards processes by
      the time each class took last run, longest first, to the least loaded process. The "everything else" process
      (the parallel classes) starts with its slowest parallel class's time, so it takes serial classes too when it has room.
      The classes of the collections in $KeepTogether stay in one process (MyriagentHub child processes: one at a time).
    - MyriagentHub.Tests starts ~180 classes at once in one process and they slow each other down 8-12x
      (8.4 minutes in one process, 2.8 minutes in three). All its classes are spread over -HubShards processes the same way.
    Timings: the TRX files of the last complete run are kept in -TimingsDir (per project). A class without a timing
    counts as $DefaultSeconds; without any timings the first run is less balanced but still runs every test once
    (tools/ci/Invoke-ShardedTests.ps1 always adds an "everything else" process).
    Retry: when at most -MaxRetry tests of a project failed they run once more in one process: with this many test
    processes at once, timing-sensitive tests lose races (a busy machine fails a handful of them every run).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/Invoke-LocalTests.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/Invoke-LocalTests.ps1 -NoBuild -Only Desktop
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    # skip `dotnet build Myriagent.slnx -c Release` (the tests run with --no-build either way)
    [switch]$NoBuild,
    [ValidateSet('All', 'Desktop', 'Hub')][string]$Only = 'All',
    # processes for the serial classes of Myriagent.Tests (plus the "everything else" one)
    [int]$SerialShards = 6,
    # processes for MyriagentHub.Tests (the last one is "everything else")
    [int]$HubShards = 3,
    [int]$MaxRetry = 15,
    [string]$ResultsDir = '',
    [string]$TimingsDir = ''
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Sharded = Join-Path $PSScriptRoot 'Invoke-ShardedTests.ps1'
$TrxNs = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
$DefaultSeconds = 5.0
$KeepTogether = @('MyriagentHubCollection')

$base = Join-Path ([System.IO.Path]::GetTempPath()) 'myriagent-tests'
if (-not $TimingsDir) { $TimingsDir = Join-Path $base 'timings' }
if (-not $ResultsDir) {
    # the default results folders: keep the newest few (each run writes tens of MB of TRX and logs)
    if (Test-Path -LiteralPath $base) {
        @(Get-ChildItem -LiteralPath $base -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}$' } | Sort-Object Name -Descending) |
            Select-Object -Skip 4 | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
    $ResultsDir = Join-Path $base (Get-Date -Format 'yyyyMMdd-HHmmss')
}
$ResultsDir = [System.IO.Path]::GetFullPath($ResultsDir)
$TimingsDir = [System.IO.Path]::GetFullPath($TimingsDir)
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

# One command-line argument for CreateProcess (Start-Process joins -ArgumentList with spaces and quotes nothing)
function ConvertTo-Argument([string]$Value) {
    if ($Value -notmatch '[\s"]' -and $Value -ne '') { return $Value }
    return '"' + (($Value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

# class full name -> seconds (sum of its test durations) from the TRX files in a folder
function Read-Timings([string]$Dir) {
    $t = @{}
    if (-not (Test-Path -LiteralPath $Dir)) { return $t }
    foreach ($file in @(Get-ChildItem -LiteralPath $Dir -Filter '*.trx' -File)) {
        $xml = New-Object System.Xml.XmlDocument
        try { $xml.Load($file.FullName) } catch { continue }
        $ns = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
        $ns.AddNamespace('t', $TrxNs)
        $classOf = @{}
        foreach ($unit in $xml.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
            $classOf[$unit.GetAttribute('id')] = $unit.SelectSingleNode('t:TestMethod', $ns).GetAttribute('className')
        }
        foreach ($r in $xml.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
            $cls = $classOf[$r.GetAttribute('testId')]
            $d = $r.GetAttribute('duration')
            if (-not $cls -or -not $d) { continue }
            $sec = [System.TimeSpan]::Parse($d, [System.Globalization.CultureInfo]::InvariantCulture).TotalSeconds
            if ($t.ContainsKey($cls)) { $t[$cls] += $sec } else { $t[$cls] = $sec }
        }
    }
    return $t
}

# class full name -> collection class name, for the classes in DisableParallelization collections
function Find-SerialClasses([string]$TestDir) {
    $files = @(Get-ChildItem -LiteralPath $TestDir -Recurse -Filter '*.cs' -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    $texts = @{}
    foreach ($f in $files) { $texts[$f.FullName] = [System.IO.File]::ReadAllText($f.FullName) }
    $serial = @{}
    foreach ($text in $texts.Values) {
        foreach ($m in [regex]::Matches($text, '\[CollectionDefinition\([^\]]*DisableParallelization\s*=\s*true[^\]]*\)\]\s*(?:\[[^\]]*\]\s*)*(?:public\s+|internal\s+)?(?:sealed\s+)?class\s+(\w+)')) {
            $serial[$m.Groups[1].Value] = $true
        }
    }
    $member = @{}
    foreach ($text in $texts.Values) {
        $ns = [regex]::Match($text, '(?m)^namespace\s+([\w.]+)')
        if (-not $ns.Success) { continue }
        foreach ($m in [regex]::Matches($text, '\[Collection\((\w+)\.Name\)\]\s*(?:\[[^\]]*\]\s*)*(?:public\s+|internal\s+)?(?:sealed\s+|abstract\s+|static\s+)*(?:partial\s+)?class\s+(\w+)')) {
            if ($serial.ContainsKey($m.Groups[1].Value)) { $member[$ns.Groups[1].Value + '.' + $m.Groups[2].Value] = $m.Groups[1].Value }
        }
    }
    return $member
}

function Get-Seconds($Timings, [string]$Class) {
    if ($Timings.ContainsKey($Class)) { return [double]$Timings[$Class] }
    return $DefaultSeconds
}

# Longest first, each unit to the least loaded process; returns the class lists of the first $Count processes
# (the "everything else" process, which starts at $RestStart seconds, gets the units it takes without listing them)
function Split-Units($Units, [int]$Count, [double]$RestStart) {
    $loads = New-Object double[] ($Count + 1)
    $lists = @()
    for ($i = 0; $i -le $Count; $i++) { $lists += , (New-Object System.Collections.Generic.List[string]) }
    $loads[$Count] = $RestStart
    foreach ($u in @($Units | Sort-Object -Property @{ Expression = 'Seconds'; Descending = $true }, @{ Expression = 'Key'; Descending = $false })) {
        $target = 0
        for ($i = 1; $i -le $Count; $i++) { if ($loads[$i] -lt $loads[$target]) { $target = $i } }
        $loads[$target] += $u.Seconds
        foreach ($c in $u.Classes) { $lists[$target].Add($c) }
    }
    $shards = @()
    for ($i = 0; $i -lt $Count; $i++) {
        if ($lists[$i].Count -gt 0) { $shards += ((@($lists[$i]) | Sort-Object) -join ',') }
    }
    return [pscustomobject]@{ Shards = $shards }
}

function Get-DesktopShards {
    $serial = Find-SerialClasses (Join-Path $Root 'tests\Myriagent.Tests')
    $timings = Read-Timings (Join-Path $TimingsDir 'desktop')
    $units = @()
    foreach ($group in @($serial.Keys | Group-Object { $serial[$_] })) {
        if ($KeepTogether -contains $group.Name) {
            $sum = 0.0
            foreach ($c in $group.Group) { $sum += Get-Seconds $timings $c }
            $units += [pscustomobject]@{ Key = $group.Name; Seconds = $sum; Classes = @($group.Group) }
        }
        else {
            foreach ($c in $group.Group) { $units += [pscustomobject]@{ Key = $c; Seconds = (Get-Seconds $timings $c); Classes = @($c) } }
        }
    }
    $parallelMax = 0.0
    foreach ($c in $timings.Keys) { if (-not $serial.ContainsKey($c) -and $timings[$c] -gt $parallelMax) { $parallelMax = $timings[$c] } }
    Write-Host ("desktop: {0} serial classes, timings for {1} classes, slowest parallel class {2:N0} s" -f $serial.Count, $timings.Count, $parallelMax)
    return Split-Units $units $SerialShards $parallelMax
}

function Get-HubShards {
    $timings = Read-Timings (Join-Path $TimingsDir 'hub')
    if ($timings.Count -eq 0) {
        Write-Host 'hub: no timings yet, splitting by namespace'
        return [pscustomobject]@{ Shards = @('MyriagentHub.Tests.Realtime', 'MyriagentHub.Tests.Sync') }
    }
    # every class with a timing is a unit (classes added since then run in the "everything else" process)
    $units = @($timings.Keys | ForEach-Object { [pscustomobject]@{ Key = $_; Seconds = [double]$timings[$_]; Classes = @($_) } })
    Write-Host ("hub: timings for {0} classes" -f $timings.Count)
    return Split-Units $units ($HubShards - 1) 0.0
}

function Start-Project([string]$Name, [string]$Project, $Split, [hashtable]$Environment) {
    $dir = Join-Path $ResultsDir $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $log = Join-Path $ResultsDir ($Name + '.log')
    $shell = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Sharded, '-Project', (Join-Path $Root $Project),
        '-ResultsDir', $dir, '-MaxRetry', [string]$MaxRetry)
    if ($Split.Shards.Count -gt 0) { $arguments += @('-Shard', ($Split.Shards -join ';')) }
    $line = ($arguments | ForEach-Object { ConvertTo-Argument $_ }) -join ' '
    if ($line.Length -gt 30000) { throw "$Name : command line too long ($($line.Length) chars); lower the shard count" }
    $process = Start-Process -FilePath $shell -ArgumentList $line -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') `
        -NoNewWindow -PassThru
    # cache the handle now: otherwise ExitCode can be empty after the process exits
    $null = $process.Handle
    Write-Host ("{0}: {1} test processes, log {2}" -f $Name, ($Split.Shards.Count + 1), $log)
    return [pscustomobject]@{ Name = $Name; Process = $process; Log = $log; Dir = $dir; Shards = ($Split.Shards.Count + 1); Started = (Get-Date) }
}

$started = Get-Date
if (-not $NoBuild) {
    Write-Host 'building Myriagent.slnx (Release)'
    & dotnet build (Join-Path $Root 'Myriagent.slnx') -c Release -nodeReuse:false --nologo -v:q | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "::error::build failed (exit $LASTEXITCODE)"; exit 1 }
}

# the child PowerShell writes its redirected output in the console's code page: make it UTF-8 so the test names
# (Chinese) read back correctly, and put the console back afterwards
$consoleEncoding = $null
try { $consoleEncoding = [Console]::OutputEncoding; [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { $consoleEncoding = $null }

$runs = @()
if ($Only -ne 'Hub') { $runs += Start-Project 'desktop' 'tests\Myriagent.Tests\Myriagent.Tests.csproj' (Get-DesktopShards) }
if ($Only -ne 'Desktop') { $runs += Start-Project 'hub' 'tests\MyriagentHub.Tests\MyriagentHub.Tests.csproj' (Get-HubShards) }

$failed = $false
foreach ($run in $runs) {
    $run.Process.WaitForExit()
    $code = $run.Process.ExitCode
    $seconds = [int]($run.Process.ExitTime - $run.Process.StartTime).TotalSeconds
    Write-Host ''
    Write-Host ("==== {0}: exit {1}, {2} s ====" -f $run.Name, $code, $seconds)
    if (Test-Path -LiteralPath $run.Log) {
        # UTF-8 when the console code page could be set above, otherwise the system code page
        $bytes = [System.IO.File]::ReadAllBytes($run.Log)
        try { $text = (New-Object System.Text.UTF8Encoding $false, $true).GetString($bytes) } catch { $text = [System.Text.Encoding]::Default.GetString($bytes) }
        $text -split '\r?\n' | Where-Object { $_ -match '^(\| |::error::|::warning::|all \d+ test processes passed|retrying )' } | ForEach-Object {
            # a shard's label is its whole class list: show how many classes instead
            $m = [regex]::Match($_, '^(\| \w+ \| )([^|]+)( \|(?: [^|]* \|){3,})$')
            if ($m.Success -and $m.Groups[2].Value.Length -gt 60) {
                Write-Host ($m.Groups[1].Value + ($m.Groups[2].Value.Split(',').Count) + ' classes' + $m.Groups[3].Value)
            }
            else { Write-Host $_ }
        }
    }
    if ($code -ne 0) { $failed = $true }
    # keep this run's TRX as the next run's timings when every process wrote one (a crashed process would lose its classes' timings)
    $trx = @(Get-ChildItem -LiteralPath $run.Dir -Filter 'shard*.trx' -File)
    if ($trx.Count -eq $run.Shards) {
        $keep = Join-Path $TimingsDir $run.Name
        if (Test-Path -LiteralPath $keep) { Remove-Item -LiteralPath $keep -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $keep | Out-Null
        foreach ($f in $trx) { Copy-Item -LiteralPath $f.FullName -Destination $keep }
    }
}
if ($consoleEncoding) { try { [Console]::OutputEncoding = $consoleEncoding } catch { } }
Write-Host ''
Write-Host ("total {0} s; logs and TRX in {1}" -f [int]((Get-Date) - $started).TotalSeconds, $ResultsDir)
if ($failed) { Write-Host 'FAILED'; exit 1 }
Write-Host 'PASSED'
exit 0
