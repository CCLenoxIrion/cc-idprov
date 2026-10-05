<#
.SYNOPSIS
    JEA function of the endpoint "CC.Onboarding.Logon" on the domain controller DC03
    (DECISIONS X5).

.DESCRIPTION
    Exactly one visible function: Set-OnbLogonScript -Sam -ContentBase64 -Sha256 [-Force] [-DryRun]
    Writes <LogonScriptDirectory>\<sam>.bat locally on DC03; DFSR replicates SYSVOL.
    Run-as: dedicated endpoint gMSA with write access to this folder only – never a virtual
    account on a DC (it would be Domain Admin).

    Hardening: no path parameter (folder from OnboardingLogonEndpoint.psd1), sam validated,
    content Base64 → ASCII only, CRLF only, SHA-256 must match, size limit, idempotent.

    Compatible with Windows PowerShell 5.1 (default JEA host) and PowerShell 7. The small helpers
    are deliberately duplicated from CCOnboarding: each endpoint server gets only its own module.
#>

Set-StrictMode -Version 2.0

$script:ConfigPath = Join-Path $PSScriptRoot 'OnboardingLogonEndpoint.psd1'

#region helpers (not visible in the JEA session)

function Get-OnbEndpointConfig {
    $config = Import-PowerShellDataFile -Path $script:ConfigPath
    foreach ($key in 'LogonScriptDirectory', 'LogonFileNamePattern', 'MaxLogonScriptBytes') {
        if (-not $config.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string] $config[$key])) {
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
    <# Returns $null if the content is acceptable, else a rejection { code, reason }. #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [byte[]] $Bytes)

    for ($i = 0; $i -lt $Bytes.Length; $i++) {
        $b = $Bytes[$i]
        if ($b -gt 0x7F) { return [pscustomobject]@{ code = 'non-ascii'; reason = 'Abgelehnt: Inhalt enthält Nicht-ASCII-Zeichen.' } }
        if ($b -eq 0x0A -and ($i -eq 0 -or $Bytes[$i - 1] -ne 0x0D)) { return [pscustomobject]@{ code = 'line-endings'; reason = 'Abgelehnt: Zeilenenden müssen CRLF sein.' } }
        if ($b -eq 0x0D -and ($i + 1 -ge $Bytes.Length -or $Bytes[$i + 1] -ne 0x0A)) { return [pscustomobject]@{ code = 'line-endings'; reason = 'Abgelehnt: Zeilenenden müssen CRLF sein.' } }
        if ($b -lt 0x20 -and $b -notin 0x09, 0x0A, 0x0D) { return [pscustomobject]@{ code = 'control-chars'; reason = 'Abgelehnt: Inhalt enthält Steuerzeichen.' } }
    }

    return $null
}

# Thin wrappers around file access so Pester can mock them.
function Test-OnbFile { param([string] $Path) Test-Path -LiteralPath $Path -PathType Leaf }
function Read-OnbFileBytes { param([string] $Path) [System.IO.File]::ReadAllBytes($Path) }
function Write-OnbFileBytes {
    param([string] $Path, [byte[]] $Bytes)
    # Write to a temp file in the same directory first, then replace: no half-written script.
    $temp = $Path + '.onbtmp'
    [System.IO.File]::WriteAllBytes($temp, $Bytes)
    Move-Item -LiteralPath $temp -Destination $Path -Force -ErrorAction Stop
}

#endregion

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

    if (-not (Test-OnbSam $Sam)) { return New-OnbResult -Status failed -Code 'invalid-sam' -Reason 'Abgelehnt: ungültiger sAMAccountName.' }
    if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') { return New-OnbResult -Status failed -Code 'invalid-hash-format' -Reason 'Abgelehnt: SHA-256 hat ein ungültiges Format.' }

    try {
        $config = Get-OnbEndpointConfig
        try {
            $bytes = [System.Convert]::FromBase64String($ContentBase64)
        }
        catch {
            return New-OnbResult -Status failed -Code 'invalid-base64' -Reason 'Abgelehnt: Inhalt ist kein gültiges Base64.'
        }

        if ($bytes.Length -gt [int] $config.MaxLogonScriptBytes) { return New-OnbResult -Status failed -Code 'too-large' -Reason 'Abgelehnt: Inhalt ist zu groß.' }
        $contentProblem = Test-OnbLogonContent -Bytes $bytes
        if ($contentProblem) { return New-OnbResult -Status failed -Code $contentProblem.code -Reason $contentProblem.reason }

        $hash = Get-OnbSha256 -Bytes $bytes
        if (-not [string]::Equals($hash, $Sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            return New-OnbResult -Status failed -Code 'hash-mismatch' -Reason 'Abgelehnt: SHA-256 stimmt nicht mit dem Inhalt überein.'
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
                return New-OnbResult -Status needsInput -Code 'logon-script-modified' -Reason ("{0} existiert mit anderem Inhalt (SHA-256 {1}, erwartet {2}); Datei wurde manuell angelegt oder geändert. Überschreiben nur per Admin-Aktion." -f $fileName, $existingHash, $hash) -Output $output
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
        return New-OnbResult -Status failed -Code 'logon-script-error' -Reason ('Anmeldeskript: Fehler ({0}).' -f $_.Exception.GetType().Name)
    }
}

Export-ModuleMember -Function Set-OnbLogonScript
