<#
.SYNOPSIS
    JEA functions of the endpoint "CC.Onboarding" on the file server DC01 (DECISIONS X5, X17).

.DESCRIPTION
    DC01 is a member server (file server), not a domain controller. Visible to the worker gMSA
    only through the role capability OnboardingHome.psrc:
      New-OnbHomeFolder -Sam -UserRight [-AdditionalAces] [-Force] [-DryRun]
      New-OnbHomeShare  -Sam [-DryRun]

    Hardening:
      * No path parameters. All locations come from OnboardingEndpoint.psd1 next to this module
        (writable by administrators only).
      * sam must match ^[a-z0-9]{1,20}$; built paths are canonicalized and must be direct
        children of the configured root (defense against traversal).
      * NTFS rights: the user right and every additional ACE must be in the endpoint's
        allowlist (HomeUserRights, HomeAceAllowlist). A compromised worker cannot grant rights
        to arbitrary accounts.
      * Every function is idempotent and supports -DryRun (report only, change nothing).
      * Results are objects { status, code, reason, plannedActions, output }; reasons never
        contain raw exception messages.

    Compatible with Windows PowerShell 5.1 (default JEA host) and PowerShell 7.
#>

Set-StrictMode -Version 2.0

$script:ConfigPath = Join-Path $PSScriptRoot 'OnboardingEndpoint.psd1'
$script:InheritAll = [System.Security.AccessControl.InheritanceFlags] 'ContainerInherit, ObjectInherit'

#region helpers (not visible in the JEA session)

function Get-OnbEndpointConfig {
    $config = Import-PowerShellDataFile -Path $script:ConfigPath
    foreach ($key in 'HomeRoot', 'ShareNamePattern', 'NetbiosDomain', 'HomeOwner', 'HomeUserRights', 'HomeAceAllowlist') {
        if (-not $config.ContainsKey($key) -or $null -eq $config[$key] -or [string]::IsNullOrWhiteSpace([string] $config[$key])) {
            throw "Endpunkt-Konfiguration unvollständig: $key"
        }
    }
    return $config
}

function New-OnbResult {
    param(
        [Parameter(Mandatory)] [ValidateSet('done', 'waiting', 'failed', 'needsInput')] [string] $Status,
        [string] $Code,
        [string] $Reason,
        [string[]] $PlannedActions = @(),
        [hashtable] $Output = @{}
    )

    if ([string]::IsNullOrEmpty($Code) -and $Status -ne 'done') { $Code = 'unspecified' }

    return [pscustomobject]@{
        status         = $Status
        code           = $Code
        reason         = $Reason
        plannedActions = @($PlannedActions)
        output         = [pscustomobject] $Output
    }
}

function Test-OnbSam {
    param([AllowNull()] [AllowEmptyString()] [string] $Sam)
    return ($null -ne $Sam) -and ($Sam -cmatch '^[a-z0-9]{1,20}$')
}

function Resolve-OnbChildPath {
    <# Builds Root\Leaf and ensures the result is a direct child of Root (no traversal). #>
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Leaf
    )

    if ($Leaf.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $Leaf -in '.', '..') {
        throw 'Ungültiger Name.'
    }

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($rootFull, $Leaf))
    $parent = [System.IO.Path]::GetDirectoryName($full).TrimEnd('\', '/')
    if (-not [string]::Equals($parent, $rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Pfad liegt außerhalb des erlaubten Stammverzeichnisses.'
    }

    return $full
}

function ConvertTo-OnbAceRequest {
    <#
    .SYNOPSIS
        Validates the requested rights against the allowlist. Returns @{ Entries = list of
        @{ Principal; Right } } or @{ Problem = result }.
    #>
    param(
        [Parameter(Mandatory)] $Config,
        [Parameter(Mandatory)] [string] $UserRight,
        [AllowNull()] [string[]] $AdditionalAces
    )

    if (@($Config.HomeUserRights) -notcontains $UserRight) {
        return @{ Problem = (New-OnbResult -Status failed -Code 'ace-not-allowed' -Reason ("Abgelehnt: Benutzerrecht '{0}' ist in der Allowlist des Endpunkts nicht erlaubt." -f $UserRight)) }
    }

    $entries = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    foreach ($item in @($AdditionalAces)) {
        if ([string]::IsNullOrEmpty($item)) { continue }
        if ($item -notmatch '^(?<principal>[^=\r\n]{1,256})=(?<right>Modify|FullControl)$') {
            return @{ Problem = (New-OnbResult -Status failed -Code 'invalid-ace' -Reason 'Abgelehnt: Recht hat kein gültiges Format (Principal=Modify|FullControl).') }
        }

        $principal = $Matches['principal']
        $right = $Matches['right']
        $allowedKey = @($Config.HomeAceAllowlist.Keys | Where-Object { [string]::Equals([string] $_, $principal, [System.StringComparison]::OrdinalIgnoreCase) }) | Select-Object -First 1
        if ($null -eq $allowedKey -or @($Config.HomeAceAllowlist[$allowedKey]) -notcontains $right) {
            return @{ Problem = (New-OnbResult -Status failed -Code 'ace-not-allowed' -Reason ("Abgelehnt: '{0}' mit Recht '{1}' ist nicht in der Allowlist des Endpunkts." -f $principal, $right)) }
        }

        if ($seen.ContainsKey($principal.ToLowerInvariant())) {
            return @{ Problem = (New-OnbResult -Status failed -Code 'invalid-ace' -Reason ("Abgelehnt: '{0}' ist doppelt angegeben." -f $principal)) }
        }

        $seen[$principal.ToLowerInvariant()] = $true
        $entries.Add(@{ Principal = $principal; Right = $right })
    }

    return @{ Entries = $entries }
}

function ConvertTo-OnbRightName {
    <# 'FullControl', 'Modify' or the raw value for anything else. #>
    param([System.Security.AccessControl.FileSystemRights] $Rights)

    $full = [System.Security.AccessControl.FileSystemRights]::FullControl
    $modify = [System.Security.AccessControl.FileSystemRights]::Modify
    if (($Rights -band $full) -eq $full) { return 'FullControl' }
    if (($Rights -band $modify) -eq $modify -and ($Rights -band ([System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor [System.Security.AccessControl.FileSystemRights]::TakeOwnership)) -eq 0) { return 'Modify' }
    return [string] $Rights
}

function Format-OnbRule {
    param([string] $Sid, [string] $Right, [string] $Inherit)
    return '{0}:{1}:{2}' -f $Sid.ToUpperInvariant(), $Right, $Inherit
}

# Thin wrappers around file/ACL/SMB/name resolution so Pester can mock them.
function Test-OnbDirectory { param([string] $Path) Test-Path -LiteralPath $Path -PathType Container }
function New-OnbDirectory { param([string] $Path) New-Item -ItemType Directory -Path $Path -ErrorAction Stop | Out-Null }
function Get-OnbSid {
    <# SID of an account name or SID string; $null if it cannot be resolved (yet). #>
    param([string] $Principal)
    try {
        if ($Principal -match '^S-1-[0-9-]+$') {
            return (New-Object System.Security.Principal.SecurityIdentifier($Principal)).Value
        }
        return ([System.Security.Principal.NTAccount] $Principal).Translate([System.Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        return $null
    }
}
function Get-OnbAclState {
    <# Protection, owner and explicit Allow/Deny rules of a folder, all by SID. #>
    param([string] $Path)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $rules = foreach ($rule in $acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier])) {
        [pscustomobject]@{
            Sid     = $rule.IdentityReference.Value
            Right   = ConvertTo-OnbRightName $rule.FileSystemRights
            Inherit = [string] $rule.InheritanceFlags
            Type    = [string] $rule.AccessControlType
        }
    }
    return [pscustomobject]@{
        Protected = $acl.AreAccessRulesProtected
        OwnerSid  = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
        Rules     = @($rules)
    }
}
function Set-OnbHomeAcl {
    <# Replaces the ACL: inheritance off, owner, exactly the given Allow rules (inherited to subfolders/files). #>
    param([string] $Path, [string] $OwnerSid, [object[]] $Rules)
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner((New-Object System.Security.Principal.SecurityIdentifier($OwnerSid)))
    foreach ($rule in $Rules) {
        $sid = New-Object System.Security.Principal.SecurityIdentifier($rule.Sid)
        $rights = [System.Security.AccessControl.FileSystemRights] $rule.Right
        $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                    $sid, $rights, $script:InheritAll, [System.Security.AccessControl.PropagationFlags]::None, 'Allow')))
    }
    Set-Acl -LiteralPath $Path -AclObject $security -ErrorAction Stop
}
function Get-OnbShare { param([string] $Name) Get-SmbShare -Name $Name -ErrorAction SilentlyContinue }
function Get-OnbShareAccess { param([string] $Name) Get-SmbShareAccess -Name $Name -ErrorAction Stop }
function New-OnbShare { param([string] $Name, [string] $Path, [string[]] $ChangeAccess, [string[]] $FullAccess)
    $parameters = @{ Name = $Name; Path = $Path; ChangeAccess = $ChangeAccess; ErrorAction = 'Stop' }
    if ($FullAccess) { $parameters['FullAccess'] = $FullAccess }
    New-SmbShare @parameters | Out-Null
}
function Grant-OnbShareAccess { param([string] $Name, [string] $Account)
    Grant-SmbShareAccess -Name $Name -AccountName $Account -AccessRight Change -Force -ErrorAction Stop | Out-Null
}

#endregion

function New-OnbHomeFolder {
    <#
    .SYNOPSIS
        Creates <HomeRoot>\<sam> with exactly the desired NTFS rights (DECISIONS X17):
        inheritance off, owner from the endpoint config, the user with -UserRight and the
        allowlisted -AdditionalAces, all inherited to subfolders and files.
    .DESCRIPTION
        Existing folder with the desired ACL → done. Different ACL → needsInput
        (home-acl-mismatch) listing the differences; with -Force the ACL is replaced.
    .PARAMETER AdditionalAces
        Entries 'Principal=Right' (Right: Modify | FullControl); each must be in HomeAceAllowlist.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Sam,
        [Parameter(Mandatory)] [ValidateSet('Modify', 'FullControl')] [string] $UserRight,
        [string[]] $AdditionalAces = @(),
        [switch] $Force,
        [switch] $DryRun
    )

    if (-not (Test-OnbSam $Sam)) { return New-OnbResult -Status failed -Code 'invalid-sam' -Reason 'Abgelehnt: ungültiger sAMAccountName.' }

    try {
        $config = Get-OnbEndpointConfig
        $request = ConvertTo-OnbAceRequest -Config $config -UserRight $UserRight -AdditionalAces $AdditionalAces
        if ($request.ContainsKey('Problem')) { return $request.Problem }

        $path = Resolve-OnbChildPath -Root $config.HomeRoot -Leaf $Sam
        $account = '{0}\{1}' -f $config.NetbiosDomain, $Sam
        $userSid = Get-OnbSid $account
        if ($null -eq $userSid) {
            return New-OnbResult -Status waiting -Code 'account-not-resolvable' -Reason 'Konto ist auf dem Fileserver noch nicht bekannt (Replikation).'
        }

        $ownerSid = Get-OnbSid ([string] $config.HomeOwner)
        if ($null -eq $ownerSid) {
            return New-OnbResult -Status failed -Code 'principal-not-resolvable' -Reason ("Besitzer '{0}' aus der Endpunkt-Konfiguration ist nicht auflösbar." -f $config.HomeOwner)
        }

        $desired = New-Object System.Collections.Generic.List[object]
        $desired.Add([pscustomobject]@{ Sid = $userSid; Right = $UserRight; Name = $account })
        foreach ($entry in $request.Entries) {
            $sid = Get-OnbSid $entry.Principal
            if ($null -eq $sid) {
                return New-OnbResult -Status failed -Code 'principal-not-resolvable' -Reason ("Principal '{0}' ist nicht auflösbar." -f $entry.Principal)
            }
            $desired.Add([pscustomobject]@{ Sid = $sid; Right = $entry.Right; Name = $entry.Principal })
        }

        $describe = ($desired | ForEach-Object { '{0}={1}' -f $_.Name, $_.Right }) -join ', '
        $planned = New-Object System.Collections.Generic.List[string]
        $output = @{ path = $path; userRight = $UserRight }

        if (-not (Test-OnbDirectory $path)) {
            $planned.Add("Ordner $path anlegen")
            $planned.Add("Rechte setzen (Vererbung aus, Besitzer $($config.HomeOwner)): $describe")
            if (-not $DryRun) {
                New-OnbDirectory $path
                Set-OnbHomeAcl -Path $path -OwnerSid $ownerSid -Rules $desired
            }
            $reason = if ($DryRun) { 'Dry-Run.' } else { 'Ordner angelegt, Rechte gesetzt.' }
            return New-OnbResult -Status done -Reason $reason -PlannedActions $planned -Output $output
        }

        $state = Get-OnbAclState $path
        $differences = New-Object System.Collections.Generic.List[string]
        if (-not $state.Protected) { $differences.Add('Vererbung ist aktiv') }
        if (-not [string]::Equals([string] $state.OwnerSid, $ownerSid, [System.StringComparison]::OrdinalIgnoreCase)) { $differences.Add('Besitzer weicht ab') }

        $inherit = [string] $script:InheritAll
        $want = @($desired | ForEach-Object { Format-OnbRule -Sid $_.Sid -Right $_.Right -Inherit $inherit })
        $have = @($state.Rules | Where-Object { $_.Type -eq 'Allow' } | ForEach-Object { Format-OnbRule -Sid $_.Sid -Right $_.Right -Inherit $_.Inherit })
        foreach ($rule in $want) { if ($have -notcontains $rule) { $differences.Add("fehlt: $rule") } }
        foreach ($rule in $have) { if ($want -notcontains $rule) { $differences.Add("zusätzlich: $rule") } }
        if (@($state.Rules | Where-Object { $_.Type -ne 'Allow' }).Count -gt 0) { $differences.Add('Deny-Einträge vorhanden') }

        if ($differences.Count -eq 0) {
            return New-OnbResult -Status done -Reason 'Ordner und Rechte entsprechen dem Soll.' -Output $output
        }

        if (-not $Force) {
            return New-OnbResult -Status needsInput -Code 'home-acl-mismatch' `
                -Reason ('Rechte am vorhandenen Home-Ordner weichen ab ({0}). Überschreiben nur per Admin-Aktion.' -f ($differences -join '; ')) -Output $output
        }

        $planned.Add("Rechte ersetzen (Vererbung aus, Besitzer $($config.HomeOwner)): $describe")
        if (-not $DryRun) { Set-OnbHomeAcl -Path $path -OwnerSid $ownerSid -Rules $desired }
        $reason = if ($DryRun) { 'Dry-Run.' } else { 'Rechte auf Soll gesetzt.' }
        return New-OnbResult -Status done -Reason $reason -PlannedActions $planned -Output $output
    }
    catch {
        return New-OnbResult -Status failed -Code 'home-folder-error' -Reason ('Home-Ordner: Fehler ({0}).' -f $_.Exception.GetType().Name)
    }
}

function New-OnbHomeShare {
    <#
    .SYNOPSIS
        Creates the hidden share for <HomeRoot>\<sam>: Change for the user, Full for
        ShareFullAccess (DECISIONS X18: stricter than the existing Everyone=Full).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Sam,
        [switch] $DryRun
    )

    if (-not (Test-OnbSam $Sam)) { return New-OnbResult -Status failed -Code 'invalid-sam' -Reason 'Abgelehnt: ungültiger sAMAccountName.' }

    try {
        $config = Get-OnbEndpointConfig
        $path = Resolve-OnbChildPath -Root $config.HomeRoot -Leaf $Sam
        $name = ([string] $config.ShareNamePattern).Replace('{sam}', $Sam)
        if ($name -notmatch '^[A-Za-z0-9._-]{1,79}\$?$') { return New-OnbResult -Status failed -Code 'invalid-share-name' -Reason 'Abgelehnt: ungültiger Freigabename (Konfiguration prüfen).' }
        $account = '{0}\{1}' -f $config.NetbiosDomain, $Sam
        $planned = New-Object System.Collections.Generic.List[string]

        $share = Get-OnbShare -Name $name
        if ($null -ne $share) {
            if (-not [string]::Equals([string] $share.Path.TrimEnd('\'), $path, [System.StringComparison]::OrdinalIgnoreCase)) {
                return New-OnbResult -Status needsInput -Code 'share-path-mismatch' -Reason "Freigabe '$name' existiert bereits mit anderem Pfad."
            }

            $hasAccess = @(Get-OnbShareAccess -Name $name | Where-Object {
                    $_.AccountName -eq $account -and $_.AccessControlType -eq 'Allow' -and $_.AccessRight -in 'Change', 'Full'
                }).Count -gt 0
            if (-not $hasAccess) {
                $planned.Add("Freigaberecht 'Ändern' für $account auf $name setzen")
                if (-not $DryRun) { Grant-OnbShareAccess -Name $name -Account $account }
            }
        }
        else {
            if (-not (Test-OnbDirectory $path) -and -not $DryRun) {
                return New-OnbResult -Status failed -Code 'home-folder-missing' -Reason 'Home-Ordner fehlt.'
            }

            $planned.Add("Freigabe $name für $path anlegen (Ändern: $account)")
            if (-not $DryRun) {
                New-OnbShare -Name $name -Path $path -ChangeAccess @($account) -FullAccess @($config.ShareFullAccess)
            }
        }

        $reason = if ($planned.Count -eq 0) { 'Freigabe und Rechte bereits vorhanden.' } elseif ($DryRun) { 'Dry-Run.' } else { 'Freigabe angelegt bzw. Rechte gesetzt.' }
        return New-OnbResult -Status done -Reason $reason -PlannedActions $planned -Output @{ share = $name }
    }
    catch {
        return New-OnbResult -Status failed -Code 'home-share-error' -Reason ('Freigabe: Fehler ({0}).' -f $_.Exception.GetType().Name)
    }
}

Export-ModuleMember -Function New-OnbHomeFolder, New-OnbHomeShare
