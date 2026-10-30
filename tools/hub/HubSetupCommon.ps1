# Shared helpers for the hub setup scripts (ASCII only, Windows PowerShell 5.1 compatible).
# Dot-source it:  . (Join-Path $PSScriptRoot 'HubSetupCommon.ps1')
#
# Ground rules shared by every script in tools/hub:
# - Only grant permissions on MyriagentHub's own folders and service; never change other folders' ACLs; never add deny entries.
# - Secrets are never written to files and never printed.

Set-StrictMode -Version 2.0

$script:HubWellKnown = @{
    Administrators = 'S-1-5-32-544'
    Users          = 'S-1-5-32-545'
    System         = 'S-1-5-18'
    LocalService   = 'S-1-5-19'
    Everyone       = 'S-1-1-0'
    AuthUsers      = 'S-1-5-11'
    ServiceLogon   = 'S-1-5-6'
    LocalAccount   = 'S-1-5-113'
}

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message)
}

function Write-Note {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ('           {0}' -f $Message)
}

function Test-IsAdmin {
    $p = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-SafeRoot {
    # Refuse obviously dangerous roots (drive roots, Windows, Program Files, Users) before any ACL work.
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Name)
    if (-not [System.IO.Path]::IsPathRooted($Path)) { throw "$Name must be an absolute path: $Path" }
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full.Length -le 3) { throw "$Name must not be a drive root: $Path" }
    $deny = @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, 'C:\Users', 'C:\inetpub') |
        Where-Object { $_ } | ForEach-Object { [System.IO.Path]::GetFullPath($_).TrimEnd('\') }
    foreach ($d in $deny) {
        if ($full -ieq $d) { throw "$Name must not be a system folder: $Path" }
    }
    return $full
}

# ---------------------------------------------------------------------------------------------
# SIDs
# ---------------------------------------------------------------------------------------------

function ConvertTo-SidString {
    # Accepts '*S-1-...', 'S-1-...' or an account name; returns the SID string.
    param([Parameter(Mandatory = $true)][string]$Identity)
    $t = $Identity.Trim()
    if ($t.StartsWith('*')) { $t = $t.Substring(1) }
    if ($t -match '^S-1-[0-9-]+$') { return $t }
    $acct = New-Object System.Security.Principal.NTAccount($t)
    return $acct.Translate([System.Security.Principal.SecurityIdentifier]).Value
}

function Get-ServiceSidString {
    # The per-service SID (NT SERVICE\<name>): S-1-5-80 + SHA-1 of the upper-case name in UTF-16LE,
    # split into five little-endian DWORDs. Same value as "sc.exe showsid <name>"; no service needed.
    param([Parameter(Mandatory = $true)][string]$ServiceName)
    $bytes = [System.Text.Encoding]::Unicode.GetBytes($ServiceName.ToUpperInvariant())
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try { $h = $sha1.ComputeHash($bytes) } finally { $sha1.Dispose() }
    $parts = for ($i = 0; $i -lt 20; $i += 4) { [System.BitConverter]::ToUInt32($h, $i) }
    return 'S-1-5-80-' + ($parts -join '-')
}

# ---------------------------------------------------------------------------------------------

function Invoke-Native {
    # Runs a native executable, returns @{ ExitCode; Output }. Throws unless the exit code is allowed.
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int[]]$AllowedExitCodes = @(0),
        [switch]$NoThrow
    )
    # Windows PowerShell 5.1 turns redirected stderr lines into terminating errors when the caller uses
    # ErrorActionPreference=Stop; keep native stderr as plain text and judge by the exit code instead.
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @ArgumentList 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $saved }
    $text = ($output -join [Environment]::NewLine)
    if (-not $NoThrow -and ($AllowedExitCodes -notcontains $code)) {
        throw ('{0} {1} failed with exit code {2}: {3}' -f (Split-Path $FilePath -Leaf), ($ArgumentList -join ' '), $code, $text)
    }
    return [pscustomobject]@{ ExitCode = $code; Output = $text }
}

function Get-ScExe { return (Join-Path $env:SystemRoot 'System32\sc.exe') }
function Get-IcaclsExe { return (Join-Path $env:SystemRoot 'System32\icacls.exe') }
function Get-SeceditExe { return (Join-Path $env:SystemRoot 'System32\secedit.exe') }

# ---------------------------------------------------------------------------------------------
# ACL helpers (icacls with *SID syntax so localized account names never matter)
# ---------------------------------------------------------------------------------------------

function Set-ProtectedAcl {
    # Removes inheritance on Path and (re)grants the given entries. Other explicit entries are kept and reported.
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Grants
    )
    $icaclsArgs = @($Path, '/inheritance:r', '/grant:r') + $Grants + @('/Q')
    [void](Invoke-Native -FilePath (Get-IcaclsExe) -ArgumentList $icaclsArgs)
}

function Grant-PathAccess {
    # Adds or replaces one explicit grant (for example '*S-1-5-21-...:(OI)(CI)RX') without touching inheritance.
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Grant)
    [void](Invoke-Native -FilePath (Get-IcaclsExe) -ArgumentList @($Path, '/grant:r', $Grant, '/Q'))
}

function Revoke-PathAccess {
    # Removes every explicit allow entry of Sid on Path (children re-inherit automatically).
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Sid)
    if (Test-Path -LiteralPath $Path) {
        [void](Invoke-Native -FilePath (Get-IcaclsExe) -ArgumentList @($Path, '/remove:g', "*$Sid", '/Q'))
    }
}

function Get-ExplicitAceSummary {
    # Lists explicit (non-inherited) allow entries as 'SID:rights'; used to report unexpected grants.
    param([Parameter(Mandatory = $true)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path
    return @($acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier]) | ForEach-Object {
            '{0}:{1}:{2}' -f $_.IdentityReference.Value, $_.AccessControlType, $_.FileSystemRights
        })
}

# ---------------------------------------------------------------------------------------------
# Service security descriptors (pure functions; tested by Test-the hub computerScripts.ps1)
# ---------------------------------------------------------------------------------------------

# CC=query config 0x1, LC=query status 0x4, SW=enumerate dependents 0x8, RP=start 0x10, WP=stop 0x20,
# LO=interrogate 0x80, RC=read control 0x20000. Deliberately no DC (change config), SD, WD, WO.
$script:ServiceStartStopQueryMask = 0x200BD

function Add-SddlAllowAce {
    # Returns the DACL-only SDDL with exactly one non-inherited allow entry for Sid carrying AccessMask.
    param(
        [Parameter(Mandatory = $true)][string]$Sddl,
        [Parameter(Mandatory = $true)][string]$Sid,
        [Parameter(Mandatory = $true)][int]$AccessMask
    )
    $sd = New-Object System.Security.AccessControl.RawSecurityDescriptor($Sddl)
    $sidObj = New-Object System.Security.Principal.SecurityIdentifier($Sid)
    $acl = $sd.DiscretionaryAcl
    if ($null -eq $acl) { throw 'Security descriptor has no DACL' }
    for ($i = $acl.Count - 1; $i -ge 0; $i--) {
        $ace = $acl[$i]
        if ($ace -is [System.Security.AccessControl.CommonAce] -and $ace.SecurityIdentifier -eq $sidObj -and
            $ace.AceQualifier -eq [System.Security.AccessControl.AceQualifier]::AccessAllowed -and -not $ace.IsInherited) {
            $acl.RemoveAce($i)
        }
    }
    $new = New-Object System.Security.AccessControl.CommonAce(
        [System.Security.AccessControl.AceFlags]::None,
        [System.Security.AccessControl.AceQualifier]::AccessAllowed,
        $AccessMask, $sidObj, $false, $null)
    $acl.InsertAce($acl.Count, $new)
    return $sd.GetSddlForm([System.Security.AccessControl.AccessControlSections]::Access)
}

function Remove-SddlAllowAce {
    # Returns the DACL-only SDDL without any non-inherited allow entry for Sid.
    param([Parameter(Mandatory = $true)][string]$Sddl, [Parameter(Mandatory = $true)][string]$Sid)
    $sd = New-Object System.Security.AccessControl.RawSecurityDescriptor($Sddl)
    $sidObj = New-Object System.Security.Principal.SecurityIdentifier($Sid)
    $acl = $sd.DiscretionaryAcl
    for ($i = $acl.Count - 1; $i -ge 0; $i--) {
        $ace = $acl[$i]
        if ($ace -is [System.Security.AccessControl.CommonAce] -and $ace.SecurityIdentifier -eq $sidObj -and
            $ace.AceQualifier -eq [System.Security.AccessControl.AceQualifier]::AccessAllowed -and -not $ace.IsInherited) {
            $acl.RemoveAce($i)
        }
    }
    return $sd.GetSddlForm([System.Security.AccessControl.AccessControlSections]::Access)
}

function Get-ServiceSddl {
    param([Parameter(Mandatory = $true)][string]$ServiceName)
    $r = Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('sdshow', $ServiceName)
    $line = ($r.Output -split "`r?`n" | Where-Object { $_ -match '^\s*D:' } | Select-Object -First 1)
    if (-not $line) { throw "sc sdshow $ServiceName returned no DACL: $($r.Output)" }
    return $line.Trim()
}

function Set-ServiceSddl {
    param([Parameter(Mandatory = $true)][string]$ServiceName, [Parameter(Mandatory = $true)][string]$Sddl)
    [void](Invoke-Native -FilePath (Get-ScExe) -ArgumentList @('sdset', $ServiceName, $Sddl))
}

# ---------------------------------------------------------------------------------------------
# User rights through secedit (Home edition has no secpol.msc). Always merge with the existing members,
# compare by SID, write back only the changed lines, re-export and verify; restore on mismatch.
# ---------------------------------------------------------------------------------------------

function ConvertFrom-SeceditInf {
    # Parses the [Privilege Rights] section of a secedit export into @{ RightName = @('raw member', ...) }.
    param([Parameter(Mandatory = $true)][AllowEmptyString()][AllowEmptyCollection()][string[]]$Lines)
    $rights = @{}
    $inSection = $false
    foreach ($raw in $Lines) {
        $line = $raw.Trim()
        if ($line -match '^\[(.+)\]$') { $inSection = ($Matches[1] -eq 'Privilege Rights'); continue }
        if (-not $inSection -or $line -eq '' -or $line.StartsWith(';')) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -lt 1) { continue }
        $name = $line.Substring(0, $eq).Trim()
        $value = $line.Substring($eq + 1).Trim()
        $members = @()
        if ($value) { $members = @($value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
        $rights[$name] = $members
    }
    return $rights
}

function ConvertTo-MemberSidList {
    # Raw secedit members ('*S-1-5-32-544', 'SomeGroup') to SID strings; untranslatable names are kept verbatim.
    param([string[]]$Members)
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($m in @($Members)) {
        if (-not $m) { continue }
        try { $out.Add((ConvertTo-SidString $m)) } catch { $out.Add($m) }
    }
    return , $out.ToArray()
}

function Get-MergedRightMembers {
    # Pure: current members (SIDs) plus Add minus Remove, order preserved, no duplicates (case-insensitive).
    param([string[]]$Current, [string[]]$Add = @(), [string[]]$Remove = @())
    $result = New-Object System.Collections.Generic.List[string]
    foreach ($m in @($Current) + @($Add)) {
        if (-not $m) { continue }
        if (@($Remove) -contains $m) { continue }
        $dup = $false
        foreach ($r in $result) { if ($r -ieq $m) { $dup = $true; break } }
        if (-not $dup) { $result.Add($m) }
    }
    return , $result.ToArray()
}

function Format-SeceditMember {
    param([Parameter(Mandatory = $true)][string]$Member)
    if ($Member -match '^S-1-[0-9-]+$') { return '*' + $Member }
    return $Member
}

function New-SeceditInfText {
    # Pure: INF text that defines only the given rights (secedit leaves undefined rights untouched).
    param([Parameter(Mandatory = $true)][hashtable]$Rights)
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('[Unicode]')
    [void]$sb.AppendLine('Unicode=yes')
    [void]$sb.AppendLine('[Version]')
    [void]$sb.AppendLine('signature="$CHICAGO$"')
    [void]$sb.AppendLine('Revision=1')
    [void]$sb.AppendLine('[Privilege Rights]')
    foreach ($name in ($Rights.Keys | Sort-Object)) {
        $members = @($Rights[$name] | ForEach-Object { Format-SeceditMember $_ })
        [void]$sb.AppendLine(('{0} = {1}' -f $name, ($members -join ',')))
    }
    return $sb.ToString()
}

function Export-UserRights {
    param([Parameter(Mandatory = $true)][string]$WorkDir)
    $inf = Join-Path $WorkDir ('export-{0}.inf' -f [Guid]::NewGuid().ToString('N'))
    [void](Invoke-Native -FilePath (Get-SeceditExe) -ArgumentList @('/export', '/cfg', $inf, '/areas', 'USER_RIGHTS', '/quiet'))
    $lines = [System.IO.File]::ReadAllLines($inf)
    Remove-Item -LiteralPath $inf -Force -ErrorAction SilentlyContinue
    return (ConvertFrom-SeceditInf -Lines $lines)
}

function Get-SeceditLogLines {
    # The secedit log (UTF-16) without the progress lines ("... (3/15) ...") and blank lines.
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    return @(Get-Content -LiteralPath $Path -Encoding Unicode | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '\(\d+/\d+\)' })
}

function Test-SameMemberSet {
    param([string[]]$A, [string[]]$B)
    $a1 = @($A | Where-Object { $_ } | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
    $b1 = @($B | Where-Object { $_ } | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
    if ($a1.Count -ne $b1.Count) { return $false }
    for ($i = 0; $i -lt $a1.Count; $i++) { if ($a1[$i] -ne $b1[$i]) { return $false } }
    return $true
}

function Get-UserRightPlan {
    # Pure: from a parsed export (right -> raw members) and the requested changes
    # (@{ 'SeServiceLogonRight' = @{ Add = @('S-1-...') }; 'SeDenyInteractiveLogonRight' = @{ Remove = @('S-1-...') } })
    # returns @{ Planned = @{ right = SIDs }; Originals = @{ right = SIDs }; Report = lines }. Only changed rights are planned.
    param([Parameter(Mandatory = $true)][hashtable]$Before, [Parameter(Mandatory = $true)][hashtable]$Changes)
    $planned = @{}
    $originals = @{}
    $report = New-Object System.Collections.Generic.List[string]
    foreach ($right in @($Changes.Keys | Sort-Object)) {
        $currentRaw = @()
        if ($Before.ContainsKey($right)) { $currentRaw = @($Before[$right]) }
        $current = ConvertTo-MemberSidList -Members $currentRaw
        # Indexer, not dot access: on a hashtable without an 'Add' key, .Add is the Hashtable.Add method.
        $spec = $Changes[$right]
        $add = @()
        $remove = @()
        if ($spec.ContainsKey('Add')) { $add = @($spec['Add'] | Where-Object { $_ } | ForEach-Object { [string]$_ }) }
        if ($spec.ContainsKey('Remove')) { $remove = @($spec['Remove'] | Where-Object { $_ } | ForEach-Object { [string]$_ }) }
        foreach ($m in @($add) + @($remove)) {
            if ($m -notmatch '^S-1-[0-9-]+$') { throw "User right changes must be SIDs, got '$m'" }
        }
        $merged = Get-MergedRightMembers -Current $current -Add $add -Remove $remove
        if (Test-SameMemberSet -A $current -B $merged) {
            $report.Add(('{0}: unchanged ({1} members)' -f $right, $current.Count))
            continue
        }
        $planned[$right] = $merged
        $originals[$right] = $current
        $report.Add(('{0}: {1} -> {2} members' -f $right, $current.Count, $merged.Count))
    }
    return @{ Planned = $planned; Originals = $originals; Report = $report.ToArray() }
}

function Update-UserRights {
    # Changes: see Get-UserRightPlan. Returns the report lines. Honors -WhatIf through the caller's ShouldProcess.
    param(
        [Parameter(Mandatory = $true)][hashtable]$Changes,
        [Parameter(Mandatory = $true)]$Cmdlet
    )
    $work = Join-Path $env:TEMP ('myriagenthub-secedit-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        $before = Export-UserRights -WorkDir $work
        $plan = Get-UserRightPlan -Before $before -Changes $Changes
        $planned = $plan.Planned
        $originals = $plan.Originals
        $report = New-Object System.Collections.Generic.List[string]
        foreach ($line in $plan.Report) { $report.Add($line) }
        if ($planned.Count -eq 0) { return $report.ToArray() }
        if (-not $Cmdlet.ShouldProcess(($planned.Keys -join ', '), 'secedit /configure (merge user rights)')) { return $report.ToArray() }

        $inf = Join-Path $work 'apply.inf'
        [System.IO.File]::WriteAllText($inf, (New-SeceditInfText -Rights $planned), [System.Text.Encoding]::Unicode)
        $db = Join-Path $work 'apply.sdb'
        $applyLog = Join-Path $work 'apply.log'
        # secedit can report "completed with warnings" through a non-zero code; the re-export below is the real check.
        # Without /log, secedit on this host writes no log at all, so keep our own and show it on failure.
        $applied = Invoke-Native -FilePath (Get-SeceditExe) -ArgumentList @('/configure', '/db', $db, '/cfg', $inf, '/areas', 'USER_RIGHTS', '/log', $applyLog, '/quiet') -NoThrow
        if ($applied.ExitCode -ne 0) {
            Write-Warning ('secedit /configure exit code {0}; verifying the result' -f $applied.ExitCode)
            foreach ($line in (Get-SeceditLogLines -Path $applyLog)) { Write-Note ('secedit: ' + $line) }
        }

        $after = Export-UserRights -WorkDir $work
        $problems = New-Object System.Collections.Generic.List[string]
        foreach ($right in $planned.Keys) {
            $actualRaw = @()
            if ($after.ContainsKey($right)) { $actualRaw = @($after[$right]) }
            $actual = ConvertTo-MemberSidList -Members $actualRaw
            if (-not (Test-SameMemberSet -A $actual -B $planned[$right])) { $problems.Add($right) }
        }
        foreach ($right in $before.Keys) {
            if ($planned.ContainsKey($right)) { continue }
            $b = ConvertTo-MemberSidList -Members @($before[$right])
            $aRaw = @()
            if ($after.ContainsKey($right)) { $aRaw = @($after[$right]) }
            if (-not (Test-SameMemberSet -A $b -B (ConvertTo-MemberSidList -Members $aRaw))) { $problems.Add("$right (not requested)") }
        }
        if ($problems.Count -gt 0) {
            $restore = Join-Path $work 'restore.inf'
            [System.IO.File]::WriteAllText($restore, (New-SeceditInfText -Rights $originals), [System.Text.Encoding]::Unicode)
            [void](Invoke-Native -FilePath (Get-SeceditExe) -ArgumentList @('/configure', '/db', (Join-Path $work 'restore.sdb'), '/cfg', $restore, '/areas', 'USER_RIGHTS', '/quiet') -NoThrow)
            throw ('User rights verification failed for: {0}. Original members were re-applied; check with secedit /export.' -f ($problems -join ', '))
        }
        return $report.ToArray()
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------------------------
# Hashing
# ---------------------------------------------------------------------------------------------

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $h = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text)) } finally { $sha.Dispose() }
    return (($h | ForEach-Object { $_.ToString('x2') }) -join '')
}
