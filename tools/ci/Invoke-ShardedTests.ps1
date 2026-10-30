<#
.SYNOPSIS
    Runs one test project as several concurrent dotnet test processes (shards), then retries the failed tests once.
    ASCII only; runs on Windows PowerShell 5.1 and PowerShell 7 (the release workflow uses pwsh on windows-latest).

.DESCRIPTION
    Why: xUnit v2 runs the collections marked DisableParallelization one after another, after everything else. In
    Myriagent.Tests they take most of the wall time (AppPaths, MyriagentHub child processes, DLP patterns, memory
    measurement), yet what they guard is process-wide state, so separate test processes can run them at the same time.

    Shards: each shard is a comma-separated list of namespace prefixes (a test belongs to it when its
    FullyQualifiedName starts with '<prefix>.'); shards are separate -Shard values or separated by ';' (pwsh -File
    passes one string). One more shard runs every test that matches none of them, so each test
    runs in exactly one shard whatever is added later. Prefixes must not contain one another. A shard that runs no test
    fails the step: a mistyped prefix would otherwise quietly move its tests into the last shard.

    Retry: when at most -MaxRetry tests failed and every failing shard listed its failures in its TRX, those tests run
    once more in one process. If they all pass, the step passes with a warning that names them (timing-sensitive tests
    that lose a race on the 4-core hosted runner). More failures, a shard that crashed without listing its failures,
    or a failure on the retry fail the step.

    Output: each shard's console output (a log group on GitHub), a table of the shards, and the failed or flaky tests;
    the same table goes to GITHUB_STEP_SUMMARY when it is set. Exit code 0 = passed, 1 = failed.

.EXAMPLE
    pwsh tools/ci/Invoke-ShardedTests.ps1 -Project tests/Myriagent.Tests/Myriagent.Tests.csproj `
        -Shard 'Myriagent.Tests.Hub', 'Myriagent.Tests.Accounts,Myriagent.Tests.Sync.Wiring'
    pwsh -File tools/ci/Invoke-ShardedTests.ps1 -Project <csproj> -Shard 'Myriagent.Tests.Hub;Myriagent.Tests.Accounts'
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string[]]$Shard = @(),
    [string]$Configuration = 'Release',
    [string]$ResultsDir = '',
    [int]$MaxRetry = 5,
    # ANDed into every shard and the retry (to try the script on a subset; the workflow does not use it)
    [string]$BaseFilter = ''
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$TrxNs = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
$FailedOutcomes = @('Failed', 'Error', 'Timeout', 'Aborted')

# VSTest filter values escape \ ( ) & | = ! ~ with a backslash
function ConvertTo-FilterValue([string]$Value) { return ($Value -replace '([\\()&|=!~])', '\$1') }

# One command-line argument for CreateProcess (Start-Process joins -ArgumentList with spaces and quotes nothing)
function ConvertTo-Argument([string]$Value) {
    if ($Value -notmatch '[\s"]' -and $Value -ne '') { return $Value }
    return '"' + (($Value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

function Join-Filter([string]$Filter) {
    if (-not $BaseFilter) { return $Filter }
    if (-not $Filter) { return $BaseFilter }
    return '(' + $BaseFilter + ')&(' + $Filter + ')'
}

function Read-Trx([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $xml = New-Object System.Xml.XmlDocument
    $xml.Load($Path)
    $ns = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
    $ns.AddNamespace('t', $TrxNs)
    $methods = @{}
    foreach ($unit in $xml.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
        $method = $unit.SelectSingleNode('t:TestMethod', $ns)
        $methods[$unit.GetAttribute('id')] = $method.GetAttribute('className') + '.' + $method.GetAttribute('name')
    }
    $executed = 0
    $failed = New-Object System.Collections.Generic.SortedSet[string] ([System.StringComparer]::Ordinal)
    foreach ($result in $xml.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
        $outcome = $result.GetAttribute('outcome')
        if ($outcome -ne 'NotExecuted') { $executed++ }
        if ($FailedOutcomes -contains $outcome) { [void]$failed.Add($methods[$result.GetAttribute('testId')]) }
    }
    return [pscustomobject]@{ Executed = $executed; Failed = @($failed) }
}

function Start-TestProcess([string]$Name, [string]$Filter) {
    $trx = $Name + '.trx'
    $log = Join-Path $ResultsDir ($Name + '.log')
    $arguments = @('test', $Project, '-c', $Configuration, '--no-build', '--nologo', '--results-directory', $ResultsDir,
        '--logger', ('trx;LogFileName=' + $trx))
    if ($Filter) { $arguments += @('--filter', $Filter) }
    $process = Start-Process -FilePath $script:Dotnet -ArgumentList ($arguments | ForEach-Object { ConvertTo-Argument $_ }) `
        -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError (Join-Path $ResultsDir ($Name + '.err.log'))
    $null = $process.Handle   # Windows PowerShell 5.1 loses ExitCode unless the handle is opened before the exit
    return [pscustomobject]@{ Name = $Name; Filter = $Filter; Process = $process; Log = $log; Trx = (Join-Path $ResultsDir $trx) }
}

function Write-Log([object]$Run) {
    foreach ($file in @($Run.Log, ($Run.Log -replace '\.log$', '.err.log'))) {
        if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file | ForEach-Object { Write-Host $_ } }
    }
}

# ---- shards ----
if (-not (Test-Path -LiteralPath $Project)) { Write-Host "::error::test project not found: $Project"; exit 1 }
$script:Dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
if (-not $ResultsDir) { $ResultsDir = Join-Path ([System.IO.Path]::GetTempPath()) ('sharded-tests-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
$ResultsDir = [System.IO.Path]::GetFullPath($ResultsDir)
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

$prefixes = @()
$shards = @()
foreach ($entry in @($Shard | ForEach-Object { $_ -split ';' } | Where-Object { $_.Trim() })) {
    $list = @($entry -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($list.Count -eq 0) { Write-Host "::error::empty shard in -Shard"; exit 1 }
    $prefixes += $list
    $shards += [pscustomobject]@{ Label = ($list -join ', '); Filter = (($list | ForEach-Object { 'FullyQualifiedName~' + (ConvertTo-FilterValue ($_ + '.')) }) -join '|') }
}
foreach ($a in $prefixes) {
    foreach ($b in $prefixes) {
        if ([object]::ReferenceEquals($a, $b)) { continue }
        if (($b + '.').StartsWith($a + '.', [System.StringComparison]::Ordinal)) { Write-Host "::error::shard prefixes overlap: '$a' and '$b'"; exit 1 }
    }
}
$rest = (@($prefixes | ForEach-Object { 'FullyQualifiedName!~' + (ConvertTo-FilterValue ($_ + '.')) }) -join '&')
$shards += [pscustomobject]@{ Label = $(if ($prefixes.Count) { 'everything else' } else { 'all tests' }); Filter = $rest }

# '~' is "contains": with the full prefix plus a dot it can only match the start of a FullyQualifiedName
$runs = @()
for ($i = 0; $i -lt $shards.Count; $i++) {
    $runs += Start-TestProcess -Name ('shard' + ($i + 1)) -Filter (Join-Filter $shards[$i].Filter)
    $runs[$i] | Add-Member -NotePropertyName Label -NotePropertyValue $shards[$i].Label
}
Write-Host ("started {0} test processes: {1}" -f $runs.Count, (($runs | ForEach-Object { $_.Name + ' (' + $_.Label + ')' }) -join '; '))

foreach ($run in $runs) {
    $run.Process.WaitForExit()
    # the process's own times: the loop waits in order, so the clock here would show the slowest shard for all of them
    $run | Add-Member -NotePropertyName Seconds -NotePropertyValue ([int]($run.Process.ExitTime - $run.Process.StartTime).TotalSeconds)
}

# ---- results ----
$failures = @()
$broken = @()
$rows = @()
foreach ($run in $runs) {
    $code = $run.Process.ExitCode
    $result = Read-Trx $run.Trx
    Write-Host ("::group::{0} ({1}): exit {2}, {3} s" -f $run.Name, $run.Label, $code, $run.Seconds)
    Write-Log $run
    Write-Host '::endgroup::'
    $executed = if ($result) { $result.Executed } else { 0 }
    $failedHere = @(if ($result) { $result.Failed })
    $rows += [pscustomobject]@{ Name = $run.Name; Label = $run.Label; Executed = $executed; Failed = $failedHere.Count; Seconds = $run.Seconds; Exit = $code }
    if (-not $result) { $broken += "$($run.Name): no TRX (exit $code)"; continue }
    if ($executed -eq 0) { $broken += "$($run.Name): no test matched ($($run.Label))"; continue }
    if ($code -ne 0 -and $failedHere.Count -eq 0) { $broken += "$($run.Name): exit $code without a failed test in the TRX (crash or abort)"; continue }
    $failures += $failedHere
}
$failures = @($failures | Sort-Object -Unique)

$summary = @('| shard | tests | executed | failed | seconds |', '|---|---|---|---|---|')
foreach ($row in $rows) { $summary += ('| {0} | {1} | {2} | {3} | {4} |' -f $row.Name, $row.Label, $row.Executed, $row.Failed, $row.Seconds) }
$summary | ForEach-Object { Write-Host $_ }

function Write-Summary([string[]]$Lines) {
    if ($env:GITHUB_STEP_SUMMARY) { ($summary + @('') + $Lines) | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}

if ($broken.Count -gt 0) {
    $broken | ForEach-Object { Write-Host "::error::$_" }
    Write-Summary (@('**Failed**: a test process did not finish normally') + ($broken | ForEach-Object { '- ' + $_ }))
    exit 1
}
if ($failures.Count -eq 0) {
    Write-Host ("all {0} test processes passed" -f $runs.Count)
    Write-Summary @('All test processes passed.')
    exit 0
}
if ($failures.Count -gt $MaxRetry) {
    $failures | ForEach-Object { Write-Host "::error::failed: $_" }
    Write-Summary (@("**Failed**: $($failures.Count) tests (more than $MaxRetry, no retry)") + ($failures | ForEach-Object { '- ' + $_ }))
    exit 1
}

# ---- retry the failed tests once ----
Write-Host ("retrying {0} failed test(s) once: {1}" -f $failures.Count, ($failures -join ', '))
$retryFilter = (@($failures | ForEach-Object { 'FullyQualifiedName=' + (ConvertTo-FilterValue $_) }) -join '|')
$retry = Start-TestProcess -Name 'retry' -Filter (Join-Filter $retryFilter)
$retry.Process.WaitForExit()
$retryResult = Read-Trx $retry.Trx
Write-Host ("::group::retry: exit {0}" -f $retry.Process.ExitCode)
Write-Log $retry
Write-Host '::endgroup::'
$stillFailing = @(if ($retryResult) { $retryResult.Failed } else { $failures })
if ($retry.Process.ExitCode -eq 0 -and $retryResult -and $retryResult.Executed -gt 0 -and $stillFailing.Count -eq 0) {
    $failures | ForEach-Object { Write-Host "::warning::failed once, passed on retry (flaky): $_" }
    Write-Summary (@("Passed after retrying $($failures.Count) flaky test(s):") + ($failures | ForEach-Object { '- ' + $_ }))
    exit 0
}
if ($stillFailing.Count -eq 0) { $stillFailing = @($failures) }
$stillFailing | ForEach-Object { Write-Host "::error::failed again on retry: $_" }
Write-Summary (@('**Failed** on retry:') + ($stillFailing | ForEach-Object { '- ' + $_ }))
exit 1
