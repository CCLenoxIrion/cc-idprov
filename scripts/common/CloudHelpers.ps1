<#
.SYNOPSIS
    Cloud helpers shared by the Entra/EXO/Teams step scripts (phase 4b). Dot-sourced, so the
    module cmdlets are called from script scope and are mockable in Pester.

.NOTES
    * App-only authentication with the certificate from LocalMachine\My (DECISIONS X12). The
      input carries only identifiers (tenant, app id, thumbprint, organization), never a secret.
    * Connect/Disconnect produce no output (| Out-Null): no token, tenant or thumbprint may end up
      in planned actions, reasons or stdout (E5).
    * Graph only via Invoke-MgGraphRequest; UPNs only URL-escaped in the path, filter values only
      via ConvertTo-ODataLiteral (DECISIONS X11).
    * Errors become sanitized safe exceptions with a fixed code; raw module messages never leave.
#>

$script:GraphBase = 'v1.0'

function Get-CloudInput {
    <# The cloud block of the step input; fails with a safe error if it is missing. #>
    param([Parameter(Mandatory)] [hashtable] $In)

    if (-not $In.ContainsKey('cloud') -or $null -eq $In['cloud']) {
        throw (New-SafeException -Code 'invalid-input' 'Cloud-Konfiguration fehlt in der Eingabe.')
    }

    return $In['cloud']
}

function ConvertTo-ODataLiteral {
    <# OData string literal for $filter values: quotes doubled, then URL-encoded (X11). #>
    [OutputType([string])]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

    return "'" + [uri]::EscapeDataString($Value.Replace("'", "''")) + "'"
}

function ConvertTo-PsLiteral {
    <# Single-quoted PowerShell literal for ready-to-run manual commands. #>
    [OutputType([string])]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-GraphUserUri {
    param([Parameter(Mandatory)] [string] $UserPrincipalName, [string] $Suffix = '')

    return '{0}/users/{1}{2}' -f $script:GraphBase, [uri]::EscapeDataString($UserPrincipalName), $Suffix
}

#region connections

function Connect-OnbGraph {
    param([Parameter(Mandatory)] [hashtable] $Auth)
    Connect-MgGraph -ClientId $Auth['appId'] -TenantId $Auth['tenantId'] -CertificateThumbprint $Auth['certificateThumbprint'] -NoWelcome -ErrorAction Stop | Out-Null
}

function Disconnect-OnbGraph {
    Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
}

function Connect-OnbExchange {
    param([Parameter(Mandatory)] [hashtable] $Auth, [Parameter(Mandatory)] [string[]] $CommandName)
    Connect-ExchangeOnline -AppId $Auth['appId'] -CertificateThumbprint $Auth['certificateThumbprint'] -Organization $Auth['organization'] `
        -ShowBanner:$false -CommandName $CommandName -ErrorAction Stop | Out-Null
}

function Disconnect-OnbExchange {
    Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}

function Connect-OnbTeams {
    param([Parameter(Mandatory)] [hashtable] $Auth)
    Connect-MicrosoftTeams -ApplicationId $Auth['appId'] -CertificateThumbprint $Auth['certificateThumbprint'] -TenantId $Auth['tenantId'] -ErrorAction Stop | Out-Null
}

function Disconnect-OnbTeams {
    Disconnect-MicrosoftTeams -ErrorAction SilentlyContinue | Out-Null
}

function Invoke-OnbCloudSession {
    <#
    .SYNOPSIS
        Connects to one cloud service, runs the body and always disconnects. A failed connect
        becomes 'cloud-auth-failed' without the module's message (it may contain ids or tokens).
        Dry-run connects as well: it reads the current state, only Invoke-StepChange is skipped.
    #>
    param(
        [Parameter(Mandatory)] [ValidateSet('Graph', 'Exchange', 'Teams')] [string] $Service,
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] [scriptblock] $Body,
        [string[]] $CommandName = @()
    )

    $auth = (Get-CloudInput $In)['auth']
    try {
        switch ($Service) {
            'Graph' { Connect-OnbGraph -Auth $auth }
            'Exchange' { Connect-OnbExchange -Auth $auth -CommandName $CommandName }
            'Teams' { Connect-OnbTeams -Auth $auth }
        }
    }
    catch {
        throw (New-SafeException -Code 'cloud-auth-failed' ('Anmeldung bei {0} fehlgeschlagen (App-Registrierung, Zertifikat und Rechte prüfen).' -f $Service))
    }

    try {
        return & $Body
    }
    finally {
        switch ($Service) {
            'Graph' { Disconnect-OnbGraph }
            'Exchange' { Disconnect-OnbExchange }
            'Teams' { Disconnect-OnbTeams }
        }
    }
}

#endregion

#region Graph

function Get-OnbHttpStatus {
    <# HTTP status of a Graph error, 0 if unknown. ZU VERIFIZIEREN: Exception-Form von Invoke-MgGraphRequest. #>
    param([Parameter(Mandatory)] $ErrorRecord)

    $exception = if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $ErrorRecord.Exception } else { $ErrorRecord }
    if ($null -ne $exception -and $exception.PSObject.Properties.Name -contains 'Response' -and $null -ne $exception.Response -and
        $exception.Response.PSObject.Properties.Name -contains 'StatusCode') {
        return [int] $exception.Response.StatusCode
    }

    return 0
}

function Invoke-OnbGraph {
    <#
    .SYNOPSIS
        Thin wrapper around Invoke-MgGraphRequest. 404 → $null; 429/5xx → waiting
        'graph-throttled'; 401/403 → 'graph-access-denied'; anything else 'graph-error' with the
        status code only.
    #>
    param(
        [ValidateSet('GET', 'POST', 'PATCH')] [string] $Method = 'GET',
        [Parameter(Mandatory)] [string] $Uri,
        $Body,
        [hashtable] $Headers
    )

    $parameters = @{ Method = $Method; Uri = $Uri; OutputType = 'HashTable'; ErrorAction = 'Stop' }
    if ($null -ne $Body) {
        $parameters['Body'] = $Body | ConvertTo-Json -Depth 10 -Compress
        $parameters['ContentType'] = 'application/json'
    }
    if ($Headers) { $parameters['Headers'] = $Headers }

    try {
        return Invoke-MgGraphRequest @parameters
    }
    catch {
        $status = Get-OnbHttpStatus $_
        if ($status -eq 404) { return $null }
        if ($status -eq 429 -or $status -ge 500) {
            throw (New-SafeException -Status waiting -Code 'graph-throttled' ('Graph vorübergehend nicht verfügbar (HTTP {0}); neuer Versuch folgt.' -f $status))
        }
        if ($status -in 401, 403) {
            throw (New-SafeException -Code 'graph-access-denied' ('Graph: Zugriff verweigert (HTTP {0}); Berechtigungen der App-Registrierung prüfen.' -f $status))
        }
        throw (New-SafeException -Code 'graph-error' ('Graph-Anfrage fehlgeschlagen (HTTP {0}).' -f $status))
    }
}

function Get-OnbCloudUser {
    <# The Entra user with the fields the steps need, or $null if it does not exist (yet). #>
    param([Parameter(Mandatory)] [string] $UserPrincipalName)

    $select = '?$select=id,userPrincipalName,accountEnabled,usageLocation,onPremisesSyncEnabled,onPremisesSecurityIdentifier,onPremisesImmutableId,assignedLicenses,licenseAssignmentStates'
    return Invoke-OnbGraph -Uri (Get-GraphUserUri -UserPrincipalName $UserPrincipalName -Suffix $select)
}

function Test-OnbOwnCloudUser {
    <#
    .SYNOPSIS
        The Entra user belongs to the request's AD account (DECISIONS X14):
        1. onPremisesSecurityIdentifier = stored objectSid (independent of the source anchor);
        2. only without stored SID: onPremisesImmutableId = Base64(objectGUID) (ZU VERIFIZIEREN,
           depends on the Entra Connect source anchor).
    #>
    param([Parameter(Mandatory)] [hashtable] $User, [Parameter(Mandatory)] [hashtable] $In)

    $sid = [string] $In['directoryObjectSid']
    if ($sid) {
        return [string] $User['onPremisesSecurityIdentifier'] -eq $sid
    }

    $guid = [string] $In['directoryObjectGuid']
    if (-not $guid) { return $false }
    $immutableId = [Convert]::ToBase64String(([guid] $guid).ToByteArray())
    return [string] $User['onPremisesImmutableId'] -ceq $immutableId
}

function Get-OwnCloudUser {
    <# Returns @{ User } for the request's own Entra user, or @{ Result } (waiting/failed/needsInput). #>
    param(
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] $Context,
        [ValidateSet('waiting', 'failed')] [string] $MissingStatus = 'failed'
    )

    $upn = [string] (Get-CloudInput $In)['upn']
    $user = Get-OnbCloudUser -UserPrincipalName $upn
    if ($null -eq $user) {
        $result = if ($MissingStatus -eq 'waiting') {
            New-StepResult -Context $Context -Status waiting -Code 'entra-user-pending' -Reason 'Benutzer in Entra noch nicht sichtbar (Synchronisierung abwarten).'
        }
        else {
            New-StepResult -Context $Context -Status failed -Code 'entra-user-not-found' -Reason 'Benutzer in Entra nicht gefunden.'
        }
        return @{ Result = $result }
    }

    if (-not (Test-OnbOwnCloudUser -User $user -In $In)) {
        return @{ Result = (New-StepResult -Context $Context -Status needsInput -Code 'foreign-cloud-account' `
                    -Reason ("Der Entra-Benutzer '{0}' gehört nicht zum AD-Konto des Auftrags (SID bzw. Immutable-ID weichen ab)." -f $upn)) }
    }

    return @{ User = $user }
}

function Get-OnbSubscribedSkus {
    $response = Invoke-OnbGraph -Uri ('{0}/subscribedSkus' -f $script:GraphBase)
    if ($null -eq $response) { return @() }
    return @($response['value'])
}

#endregion

#region Exchange / Teams lookups (mockable, $null if not found)

function Get-OnbMailbox {
    <# ZU VERIFIZIEREN: Get-EXOMailbox liefert bei "nicht gefunden" einen Fehler, der hier zu $null wird. #>
    param([Parameter(Mandatory)] [string] $Identity)
    return Get-EXOMailbox -Identity $Identity -ErrorAction SilentlyContinue
}

function Get-OnbCsUser {
    param([Parameter(Mandatory)] [string] $Identity)
    return Get-CsOnlineUser -Identity $Identity -ErrorAction SilentlyContinue
}

#endregion

function Test-HasPhone {
    <# Telephony steps run only with an extension (DECISIONS T1). #>
    param([Parameter(Mandatory)] [hashtable] $Cloud)
    return -not [string]::IsNullOrWhiteSpace([string] $Cloud['phoneE164'])
}
