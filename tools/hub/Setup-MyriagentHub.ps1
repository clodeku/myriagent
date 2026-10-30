<#
.SYNOPSIS
    Prepares this computer to run the MyriagentHub Windows service (one-time, idempotent). ASCII only, Windows PowerShell 5.1.

.DESCRIPTION
    Run as Administrator on the hub computer:

        powershell -NoProfile -ExecutionPolicy Bypass -File tools\hub\Setup-MyriagentHub.ps1 -Domain hub.example.com -WhatIf
        powershell -NoProfile -ExecutionPolicy Bypass -File tools\hub\Setup-MyriagentHub.ps1 -Domain hub.example.com -AcmeEmail you@example.com

    Steps (every step checks the current state first, so re-running is safe; -WhatIf changes nothing):
      0. Report what already listens on TCP 443 (Kestrel needs the port for itself).
      1. Folders under InstallRoot and DataRoot (the parent of InstallRoot is created with its own protected ACL if missing).
      2. Service MyriagentHub (virtual account NT SERVICE\MyriagentHub, delayed auto start, restart on failure,
         service SID type, TMP/TEMP pointing to DataRoot\tmp).
      3. ACLs on MyriagentHub's own folders only (inheritance removed; Administrators and SYSTEM full control,
         the service SID read/execute on InstallRoot and modify on DataRoot).
      4. Logon probe (only before the first deployment): if the virtual account cannot log on (error 1069),
         grant SeServiceLogonRight through secedit; if that is not enough, fall back to
         NT AUTHORITY\LocalService with SID type unrestricted (ACLs keep targeting NT SERVICE\MyriagentHub).
      5. Firewall rule MyriagentHub-HTTPS-In: TCP 443 inbound, scoped to the MyriagentHub service, edge traversal blocked.
      6. Application event log source MyriagentHub.
      7. DataRoot\config\appsettings.Production.json if it does not exist yet (domain, data root, port 443, Let's Encrypt).

    The service starts on the first deployment (tools/hub/Update-MyriagentHub.ps1).
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallRoot = 'C:\Services\MyriagentHub',
    [string]$DataRoot = 'C:\ProgramData\MyriagentHub',
    [string]$ServiceName = 'MyriagentHub',
    [Parameter(Mandatory = $true)][string]$Domain,
    [string]$AcmeDirectory = 'https://acme-v02.api.letsencrypt.org/directory',
    [string]$AcmeEmail = '',
    [ValidateSet('unrestricted', 'restricted')][string]$ServiceSidType = 'unrestricted',
    [switch]$SkipLogonProbe
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HubSetupCommon.ps1')
$script:Cmdlet = $PSCmdlet
function Confirm-Change([string]$Target, [string]$Action) { return $script:Cmdlet.ShouldProcess($Target, $Action) }

$isAdmin = Test-IsAdmin
$dryRun = [bool]$WhatIfPreference
# Load the modules up front with WhatIf off, otherwise module auto-loading prints "What if: Set Alias" noise.
$WhatIfPreference = $false
Import-Module CimCmdlets, NetTCPIP, NetSecurity, Microsoft.PowerShell.LocalAccounts -ErrorAction SilentlyContinue
$WhatIfPreference = $dryRun
if (-not $isAdmin -and -not $dryRun) { throw 'Run as Administrator (or add -WhatIf for a dry run).' }
if (-not $isAdmin) { Write-Warning 'Not elevated: some service details may be unreadable; nothing will be changed.' }

$InstallRoot = Assert-SafeRoot -Path $InstallRoot -Name 'InstallRoot'
$DataRoot = Assert-SafeRoot -Path $DataRoot -Name 'DataRoot'
foreach ($p in @($InstallRoot, $DataRoot)) {
    if ($p.Contains(' ')) { throw "Paths with spaces are not supported (service binPath stays unquoted-safe): $p" }
}
if ($ServiceName -notmatch '^[A-Za-z][A-Za-z0-9_.-]{1,63}$') { throw "Invalid service name: $ServiceName" }

$serviceSid = Get-ServiceSidString -ServiceName $ServiceName
$exePath = Join-Path $InstallRoot 'current\MyriagentHub.exe'
$binPath = '{0} --data {1}' -f $exePath, $DataRoot
$sidAdmins = '*' + $script:HubWellKnown.Administrators
$sidSystem = '*' + $script:HubWellKnown.System
$sidUsers = '*' + $script:HubWellKnown.Users

Write-Step ('Setup-MyriagentHub  service={0}  sid={1}  install={2}  data={3}  dryRun={4}' -f $ServiceName, $serviceSid, $InstallRoot, $DataRoot, $dryRun)

# ------------------------------------------------------------------------------------------------
# 0. What listens on 443
# ------------------------------------------------------------------------------------------------
Write-Step '0. TCP 443'
$listen443 = @(Get-NetTCPConnection -State Listen -LocalPort 443 -ErrorAction SilentlyContinue)
foreach ($l in $listen443) {
    $proc = Get-Process -Id $l.OwningProcess -ErrorAction SilentlyContinue
    $pname = ''
    if ($proc) { $pname = $proc.ProcessName }
    Write-Note ('listen   [{0}]:443 pid={1} {2}' -f $l.LocalAddress, $l.OwningProcess, $pname)
}
if ($listen443.Count -eq 0) { Write-Note 'listen   nothing listens on TCP 443' }
$foreign443 = @($listen443 | Where-Object {
        $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        -not ($p -and $p.ProcessName -eq 'MyriagentHub')
    })
if ($foreign443.Count -gt 0) {
    Write-Warning 'TCP 443 is already used by something other than MyriagentHub (Kestrel would fail with 10013). Resolve before the first deployment.'
}

# ------------------------------------------------------------------------------------------------
# 1. Folders
# ------------------------------------------------------------------------------------------------
Write-Step '1. Folders'
$servicesParent = Split-Path $InstallRoot -Parent
if (-not (Test-Path -LiteralPath $servicesParent)) {
    if (Confirm-Change $servicesParent 'create folder (inheritance removed; Administrators/SYSTEM full, Users read)') {
        New-Item -ItemType Directory -Path $servicesParent -Force | Out-Null
        Set-ProtectedAcl -Path $servicesParent -Grants @("${sidAdmins}:(OI)(CI)F", "${sidSystem}:(OI)(CI)F", "${sidUsers}:(OI)(CI)RX")
    }
}
else { Write-Note ('{0} exists; its ACL is left as is' -f $servicesParent) }

$installDirs = @($InstallRoot) + @('releases', 'tools', 'logs' | ForEach-Object { Join-Path $InstallRoot $_ })
$dataDirs = @($DataRoot) + @('config', 'db', 'transcripts', 'blobs', 'cache', 'backups', 'secrets', 'status',
    'admin-inbox', 'admin-inbox\deploy', 'setup', 'diag', 'tmp' | ForEach-Object { Join-Path $DataRoot $_ })
foreach ($d in $installDirs + $dataDirs) {
    if (Test-Path -LiteralPath $d) { continue }
    if (Confirm-Change $d 'create folder') { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# ------------------------------------------------------------------------------------------------
# 2. Service
# ------------------------------------------------------------------------------------------------
Write-Step '2. Service'
$svc = Get-CimInstance Win32_Service -Filter ("Name='{0}'" -f $ServiceName) -ErrorAction SilentlyContinue
$desiredAccount = "NT SERVICE\$ServiceName"
if (-not $svc) {
    if (Confirm-Change $ServiceName ("sc create (binPath={0}, obj={1}, start=delayed-auto)" -f $binPath, $desiredAccount)) {
        [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('create', $ServiceName, 'binPath=', $binPath, 'obj=', $desiredAccount,
                'start=', 'delayed-auto', 'DisplayName=', 'Myriagent Hub'))
    }
}
else {
    Write-Note ('exists: path={0} account={1} start={2} state={3}' -f $svc.PathName, $svc.StartName, $svc.StartMode, $svc.State)
    if ($svc.StartName -ieq 'NT AUTHORITY\LocalService') {
        $desiredAccount = 'NT AUTHORITY\LocalService'
        Write-Note 'keeping the LocalService fallback chosen by an earlier run'
    }
    $delayed = $true
    $delayedProp = $svc.PSObject.Properties['DelayedAutoStart']
    if ($delayedProp) { $delayed = [bool]$delayedProp.Value }
    $needs = ($svc.PathName -ne $binPath) -or ($svc.StartMode -ne 'Auto') -or (-not $delayed) -or ($svc.StartName -ine $desiredAccount)
    if ($needs -and (Confirm-Change $ServiceName 'sc config (binPath, delayed-auto, account)')) {
        [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('config', $ServiceName, 'binPath=', $binPath, 'start=', 'delayed-auto',
                'obj=', $desiredAccount, 'DisplayName=', 'Myriagent Hub'))
    }
}
$sidTypeToSet = $ServiceSidType
if ($desiredAccount -ieq 'NT AUTHORITY\LocalService') { $sidTypeToSet = 'unrestricted' }
if (Confirm-Change $ServiceName ("sidtype {0}; description; TMP/TEMP (restart-on-failure is set after the logon probe)" -f $sidTypeToSet)) {
    [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('sidtype', $ServiceName, $sidTypeToSet))
    [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('description', $ServiceName, ('Clode Myriagent sync hub ({0}, Kestrel 443)' -f $Domain)))
    $tmp = Join-Path $DataRoot 'tmp'
    New-ItemProperty -Path ("HKLM:\SYSTEM\CurrentControlSet\Services\{0}" -f $ServiceName) -Name 'Environment' -PropertyType MultiString `
        -Value @("TMP=$tmp", "TEMP=$tmp") -Force | Out-Null
}

# ------------------------------------------------------------------------------------------------
# 3. ACLs on MyriagentHub's own folders only
# ------------------------------------------------------------------------------------------------
Write-Step '3. ACLs (own folders only)'
$aclPlan = @(
    @{ Path = $InstallRoot; Grants = @("${sidAdmins}:(OI)(CI)F", "${sidSystem}:(OI)(CI)F", ('*{0}:(OI)(CI)RX' -f $serviceSid)) },
    @{ Path = $DataRoot; Grants = @("${sidAdmins}:(OI)(CI)F", "${sidSystem}:(OI)(CI)F", ('*{0}:(OI)(CI)M' -f $serviceSid)) }
)
foreach ($item in $aclPlan) {
    if (Confirm-Change $item.Path ('remove inheritance; grant {0}' -f ($item.Grants -join ' '))) {
        Set-ProtectedAcl -Path $item.Path -Grants $item.Grants
    }
    if (Test-Path -LiteralPath $item.Path) {
        $known = @($script:HubWellKnown.Administrators, $script:HubWellKnown.System, $serviceSid)
        $extra = @(Get-ExplicitAceSummary -Path $item.Path | Where-Object { $known -notcontains (($_ -split ':')[0]) })
        foreach ($e in $extra) { Write-Note ('other explicit entry kept on {0}: {1}' -f $item.Path, $e) }
    }
}

# ------------------------------------------------------------------------------------------------
# 4. Logon probe and fallbacks (only before the first deployment, while the service is stopped)
# ------------------------------------------------------------------------------------------------
function Invoke-ServiceLogonProbe {
    # Temporarily points the service at "cmd.exe /c exit 0" and starts it. Error 1069 means the account
    # cannot log on as a service; 1053/1067/0 mean the logon itself worked. The real binPath is restored.
    # sc.exe messages are localized, so only its exit code (= the Win32 error) is used.
    $probeBin = '{0} /c exit 0' -f (Join-Path $env:SystemRoot 'System32\cmd.exe')
    [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('config', $ServiceName, 'binPath=', $probeBin))
    try {
        $r = Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('start', $ServiceName) -NoThrow
        $code = [int]$r.ExitCode
        $deadline = (Get-Date).AddSeconds(45)
        do {
            Start-Sleep -Seconds 1
            $state = (Get-Service -Name $ServiceName).Status
        } while ($state -ne 'Stopped' -and (Get-Date) -lt $deadline)
        return $code
    }
    finally {
        [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('config', $ServiceName, 'binPath=', $binPath))
    }
}

Write-Step '4. Logon probe'
$svcNow = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($SkipLogonProbe) { Write-Note 'skipped (-SkipLogonProbe)' }
elseif ($dryRun -or -not $svcNow) { Write-Note 'would run: temporary binPath cmd.exe /c exit 0, sc start, interpret 1069; fallbacks secedit then LocalService' }
elseif (Test-Path -LiteralPath $exePath) { Write-Note 'skipped: a release is already deployed (current\MyriagentHub.exe exists)' }
elseif ($svcNow.Status -ne 'Stopped') { Write-Note ('skipped: service state is {0}' -f $svcNow.Status) }
else {
    $code = Invoke-ServiceLogonProbe
    Write-Note ('probe result code={0} (1069 = logon failure; 1053/1067/0 = logon works)' -f $code)
    if ($code -eq 1069) {
        Write-Step '4a. Granting SeServiceLogonRight to the service SID through secedit'
        $report = Update-UserRights -Changes @{ 'SeServiceLogonRight' = @{ Add = @($serviceSid) } } -Cmdlet $PSCmdlet
        foreach ($line in $report) { Write-Note $line }
        $code = Invoke-ServiceLogonProbe
        Write-Note ('probe after secedit: code={0}' -f $code)
    }
    if ($code -eq 1069) {
        Write-Step '4b. Falling back to NT AUTHORITY\LocalService with SID type unrestricted'
        [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('config', $ServiceName, 'obj=', 'NT AUTHORITY\LocalService'))
        [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('sidtype', $ServiceName, 'unrestricted'))
        $code = Invoke-ServiceLogonProbe
        Write-Note ('probe with LocalService: code={0}' -f $code)
        if ($code -eq 1069) { throw 'The service still cannot log on (1069). Check the System event log (Service Control Manager) and docs/hub-setup.md.' }
    }
    if (@(0, 1053, 1067, 1069) -notcontains $code) { Write-Warning ("Inconclusive probe code $code; check the System event log before the first deployment.") }
}
# Restart-on-failure only after the probe, so the SCM does not keep restarting the probe binary.
if (Confirm-Change $ServiceName 'failure actions restart 5s/30s/120s (reset 1 day); failureflag 1') {
    [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/30000/restart/120000'))
    [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('failureflag', $ServiceName, '1'))
}

# ------------------------------------------------------------------------------------------------
# 5. Firewall
# ------------------------------------------------------------------------------------------------
Write-Step '5. Firewall rule MyriagentHub-HTTPS-In (TCP 443, service-scoped)'
$ruleName = 'MyriagentHub-HTTPS-In'
$existing = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
$ruleDisplay = 'Myriagent Hub HTTPS (TCP 443)'
$ruleArgs = @{
    Direction = 'Inbound'; Protocol = 'TCP'; LocalPort = '443'; Service = $ServiceName; Program = 'Any'
    Action = 'Allow'; Profile = 'Any'; EdgeTraversalPolicy = 'Block'; RemoteAddress = 'Any'; LocalAddress = 'Any'; Enabled = 'True'
}
if (-not $existing) {
    if (Confirm-Change $ruleName 'New-NetFirewallRule') { New-NetFirewallRule -Name $ruleName -DisplayName $ruleDisplay @ruleArgs | Out-Null }
}
elseif (Confirm-Change $ruleName 'Set-NetFirewallRule (reconcile to the desired values)') {
    Set-NetFirewallRule -Name $ruleName -NewDisplayName $ruleDisplay @ruleArgs | Out-Null
}

# ------------------------------------------------------------------------------------------------
# 6. Event source
# ------------------------------------------------------------------------------------------------
Write-Step '6. Event log source'
$sourceExists = $null
try { $sourceExists = [System.Diagnostics.EventLog]::SourceExists($ServiceName) } catch { $sourceExists = $null }
if ($sourceExists -eq $true) { Write-Note 'exists' }
elseif (Confirm-Change $ServiceName 'New-EventLog -LogName Application -Source') { New-EventLog -LogName Application -Source $ServiceName }

# ------------------------------------------------------------------------------------------------
# 7. Production configuration (only when missing; never overwritten)
# ------------------------------------------------------------------------------------------------
Write-Step '7. appsettings.Production.json'
$configPath = Join-Path $DataRoot 'config\appsettings.Production.json'
if (Test-Path -LiteralPath $configPath) {
    Write-Note 'exists; left untouched'
    # Hub:Acme:Enabled defaults to false in the service (dev and tests must not call Let's Encrypt); production must turn it on.
    $existing = $null
    try { $existing = [System.IO.File]::ReadAllText($configPath) | ConvertFrom-Json } catch { }
    if (-not ($existing -and $existing.Hub -and $existing.Hub.Acme -and $existing.Hub.Acme.Enabled -eq $true)) {
        Write-Warning ('{0} does not set Hub:Acme:Enabled=true: the service keeps its self-signed certificate. Add "Enabled": true under Hub.Acme and restart the service.' -f $configPath)
    }
}
elseif (Confirm-Change $configPath 'write initial configuration') {
    $config = [ordered]@{
        Hub = [ordered]@{
            ServerDomain = $Domain
            DataDir      = $DataRoot
            HttpsPort    = 443
            # Enabled is required: the service default is false so that development and tests never call Let's Encrypt
            Acme         = [ordered]@{ Enabled = $true; Directory = $AcmeDirectory; Email = $AcmeEmail }
        }
    }
    [System.IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
}

Write-Step 'Done. Next: build and install the first release with tools/hub/Update-MyriagentHub.ps1 (docs/hub-setup.md).'
exit 0
