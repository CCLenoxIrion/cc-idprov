<#
.SYNOPSIS
    JEA functions of the endpoint "CC.Onboarding" on DC01 (DECISIONS X5).

.DESCRIPTION
    Visible to the worker gMSA only through the role capability OnboardingDc.psrc:
      New-OnbHomeFolder  -Sam [-DryRun]
      New-OnbHomeShare   -Sam [-DryRun]
      Set-OnbLogonScript -Sam -ContentBase64 -Sha256 [-Force] [-DryRun]

    Hardening:
      * No path parameters. All locations come from OnboardingEndpoint.psd1 next to this module
        (writable by administrators only).
      * sam must match ^[a-z0-9]{1,20}$; built paths are canonicalized and must be direct
        children of the configured root (defense against traversal).
      * Logon script content: Base64, ASCII only, CRLF only, SHA-256 must match.
      * Every function is idempotent and supports -DryRun (report only, change nothing).
      * Results are objects { status, reason, plannedActions, output }; reasons never contain
        raw exception messages.

    Compatible with Windows PowerShell 5.1 (default JEA host) and PowerShell 7.
#>

Set-StrictMode -Version 2.0

$script:ConfigPath = Join-Path $PSScriptRoot 'OnboardingEndpoint.psd1'

#region helpers (not visible in the JEA session)

function Get-OnbEndpointConfig {
    $config = Import-PowerShellDataFile -Path $script:ConfigPath
    foreach ($key in 'HomeRoot', 'ShareNamePattern', 'NetbiosDomain', 'LogonScriptDirectory', 'LogonFileNamePattern', 'MaxLogonScriptBytes') {
        if (-not $config.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string] $config[$key])) {
            throw "Endpunkt-Konfiguration unvollständig: $key"
        }
    }
    return $config
}

function New-OnbResult {
    param(
        [Parameter(Mandatory)] [ValidateSet('done', 'waiting', 'failed', 'needsInput')] [string] $Status,
        [string] $Reason,
        [string[]] $PlannedActions = @(),
        [hashtable] $Output = @{}
    )

    return [pscustomobject]@{
        status         = $Status
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

function Get-OnbSha256 {
    param([Parameter(Mandatory)] [byte[]] $Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally {
        $sha.Dispose()
    }
}

function Test-OnbLogonContent {
    <# Returns $null if the content is acceptable, else a rejection reason. #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [byte[]] $Bytes)

    for ($i = 0; $i -lt $Bytes.Length; $i++) {
        $b = $Bytes[$i]
        if ($b -gt 0x7F) { return 'Abgelehnt: Inhalt enthält Nicht-ASCII-Zeichen.' }
        if ($b -eq 0x0A -and ($i -eq 0 -or $Bytes[$i - 1] -ne 0x0D)) { return 'Abgelehnt: Zeilenenden müssen CRLF sein.' }
        if ($b -eq 0x0D -and ($i + 1 -ge $Bytes.Length -or $Bytes[$i + 1] -ne 0x0A)) { return 'Abgelehnt: Zeilenenden müssen CRLF sein.' }
        if ($b -lt 0x20 -and $b -notin 0x09, 0x0A, 0x0D) { return 'Abgelehnt: Inhalt enthält Steuerzeichen.' }
    }

    return $null
}

# Thin wrappers around file/ACL/SMB access so Pester can mock them.
function Test-OnbDirectory { param([string] $Path) Test-Path -LiteralPath $Path -PathType Container }
function New-OnbDirectory { param([string] $Path) New-Item -ItemType Directory -Path $Path -ErrorAction Stop | Out-Null }
function Test-OnbFile { param([string] $Path) Test-Path -LiteralPath $Path -PathType Leaf }
function Read-OnbFileBytes { param([string] $Path) [System.IO.File]::ReadAllBytes($Path) }
function Write-OnbFileBytes {
    param([string] $Path, [byte[]] $Bytes)
    # Write to a temp file in the same directory first, then replace: no half-written script.
    $temp = $Path + '.onbtmp'
    [System.IO.File]::WriteAllBytes($temp, $Bytes)
    Move-Item -LiteralPath $temp -Destination $Path -Force -ErrorAction Stop
}
function Get-OnbAcl { param([string] $Path) Get-Acl -LiteralPath $Path -ErrorAction Stop }
function Set-OnbAcl { param([string] $Path, $Acl) Set-Acl -LiteralPath $Path -AclObject $Acl -ErrorAction Stop }
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
function Test-OnbAccountResolvable {
    param([string] $Account)
    try {
        [void] ([System.Security.Principal.NTAccount] $Account).Translate([System.Security.Principal.SecurityIdentifier])
        return $true
    }
    catch {
        return $false
    }
}

#endregion

function New-OnbHomeFolder {
    <#
    .SYNOPSIS
        Creates <HomeRoot>\<sam> and grants the user Modify (inherited to subfolders/files).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Sam,
        [switch] $DryRun
    )

    if (-not (Test-OnbSam $Sam)) { return New-OnbResult -Status failed -Reason 'Abgelehnt: ungültiger sAMAccountName.' }

    try {
        $config = Get-OnbEndpointConfig
        $path = Resolve-OnbChildPath -Root $config.HomeRoot -Leaf $Sam
        $account = '{0}\{1}' -f $config.NetbiosDomain, $Sam
        $planned = New-Object System.Collections.Generic.List[string]

        if (-not (Test-OnbAccountResolvable $account)) {
            return New-OnbResult -Status waiting -Reason 'Konto ist auf diesem Domänencontroller noch nicht bekannt (Replikation).'
        }

        $exists = Test-OnbDirectory $path
        if (-not $exists) {
            $planned.Add("Ordner $path anlegen")
            if (-not $DryRun) { New-OnbDirectory $path }
        }

        if ($exists -or -not $DryRun) {
            $acl = Get-OnbAcl $path
            $hasRule = @($acl.Access | Where-Object {
                    $_.IdentityReference.Value -eq $account -and
                    $_.AccessControlType -eq 'Allow' -and
                    ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Modify) -eq [System.Security.AccessControl.FileSystemRights]::Modify -and
                    ($_.InheritanceFlags -band 3) -eq 3
                }).Count -gt 0
            if (-not $hasRule) {
                $planned.Add("Recht 'Ändern' für $account auf $path setzen")
                if (-not $DryRun) {
                    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
                        $account, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
                    $acl.AddAccessRule($rule)
                    Set-OnbAcl -Path $path -Acl $acl
                }
            }
        }
        else {
            $planned.Add("Recht 'Ändern' für $account auf $path setzen")
        }

        $reason = if ($planned.Count -eq 0) { 'Ordner und Rechte bereits vorhanden.' } elseif ($DryRun) { 'Dry-Run.' } else { 'Ordner angelegt bzw. Rechte gesetzt.' }
        return New-OnbResult -Status done -Reason $reason -PlannedActions $planned -Output @{ path = $path }
    }
    catch {
        return New-OnbResult -Status failed -Reason ('Home-Ordner: Fehler ({0}).' -f $_.Exception.GetType().Name)
    }
}

function New-OnbHomeShare {
    <#
    .SYNOPSIS
        Creates the hidden share for <HomeRoot>\<sam> with Change for the user.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Sam,
        [switch] $DryRun
    )

    if (-not (Test-OnbSam $Sam)) { return New-OnbResult -Status failed -Reason 'Abgelehnt: ungültiger sAMAccountName.' }

    try {
        $config = Get-OnbEndpointConfig
        $path = Resolve-OnbChildPath -Root $config.HomeRoot -Leaf $Sam
        $name = ([string] $config.ShareNamePattern).Replace('{sam}', $Sam)
        if ($name -notmatch '^[A-Za-z0-9._-]{1,79}\$?$') { return New-OnbResult -Status failed -Reason 'Abgelehnt: ungültiger Freigabename (Konfiguration prüfen).' }
        $account = '{0}\{1}' -f $config.NetbiosDomain, $Sam
        $planned = New-Object System.Collections.Generic.List[string]

        $share = Get-OnbShare -Name $name
        if ($null -ne $share) {
            if (-not [string]::Equals([string] $share.Path.TrimEnd('\'), $path, [System.StringComparison]::OrdinalIgnoreCase)) {
                return New-OnbResult -Status needsInput -Reason "Freigabe '$name' existiert bereits mit anderem Pfad."
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
                return New-OnbResult -Status failed -Reason 'Home-Ordner fehlt.'
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
        return New-OnbResult -Status failed -Reason ('Freigabe: Fehler ({0}).' -f $_.Exception.GetType().Name)
    }
}

function Set-OnbLogonScript {
    <#
    .SYNOPSIS
        Writes <LogonScriptDirectory>\<sam>.bat. Same hash → nothing to do; different content
        without -Force → needsInput (file was created or changed by hand).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Sam,
        [Parameter(Mandatory)] [string] $ContentBase64,
        [Parameter(Mandatory)] [string] $Sha256,
        [switch] $Force,
        [switch] $DryRun
    )

    if (-not (Test-OnbSam $Sam)) { return New-OnbResult -Status failed -Reason 'Abgelehnt: ungültiger sAMAccountName.' }
    if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') { return New-OnbResult -Status failed -Reason 'Abgelehnt: SHA-256 hat ein ungültiges Format.' }

    try {
        $config = Get-OnbEndpointConfig
        try {
            $bytes = [System.Convert]::FromBase64String($ContentBase64)
        }
        catch {
            return New-OnbResult -Status failed -Reason 'Abgelehnt: Inhalt ist kein gültiges Base64.'
        }

        if ($bytes.Length -gt [int] $config.MaxLogonScriptBytes) { return New-OnbResult -Status failed -Reason 'Abgelehnt: Inhalt ist zu groß.' }
        $contentProblem = Test-OnbLogonContent -Bytes $bytes
        if ($contentProblem) { return New-OnbResult -Status failed -Reason $contentProblem }

        $hash = Get-OnbSha256 -Bytes $bytes
        if (-not [string]::Equals($hash, $Sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            return New-OnbResult -Status failed -Reason 'Abgelehnt: SHA-256 stimmt nicht mit dem Inhalt überein.'
        }

        $fileName = ([string] $config.LogonFileNamePattern).Replace('{sam}', $Sam)
        $path = Resolve-OnbChildPath -Root $config.LogonScriptDirectory -Leaf $fileName
        $output = @{ fileName = $fileName; sha256 = $hash; bytes = $bytes.Length }
        $planned = New-Object System.Collections.Generic.List[string]

        if (Test-OnbFile $path) {
            $existingHash = Get-OnbSha256 -Bytes (Read-OnbFileBytes $path)
            if ($existingHash -eq $hash) {
                return New-OnbResult -Status done -Reason "Datei vorhanden, SHA-256 $hash." -Output $output
            }

            if (-not $Force) {
                return New-OnbResult -Status needsInput -Reason ("{0} existiert mit anderem Inhalt (SHA-256 {1}, erwartet {2}); Datei wurde manuell angelegt oder geändert. Überschreiben nur per Admin-Aktion." -f $fileName, $existingHash, $hash) -Output $output
            }

            $planned.Add("$fileName überschreiben (SHA-256 $hash)")
        }
        else {
            $planned.Add("$fileName schreiben (SHA-256 $hash)")
        }

        if (-not $DryRun) { Write-OnbFileBytes -Path $path -Bytes $bytes }
        $reason = if ($DryRun) { 'Dry-Run.' } else { "Datei geschrieben, SHA-256 $hash." }
        return New-OnbResult -Status done -Reason $reason -PlannedActions $planned -Output $output
    }
    catch {
        return New-OnbResult -Status failed -Reason ('Anmeldeskript: Fehler ({0}).' -f $_.Exception.GetType().Name)
    }
}

Export-ModuleMember -Function New-OnbHomeFolder, New-OnbHomeShare, Set-OnbLogonScript
