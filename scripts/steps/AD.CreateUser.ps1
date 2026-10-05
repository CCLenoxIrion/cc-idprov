#Requires -Version 7.2
<#
.SYNOPSIS
    AD.CreateUser (SPEC §7): creates the disabled account in the area OU, or verifies and
    corrects an existing own account. Input JSON on stdin, result JSON on stdout.

.NOTES
    * The initial password arrives only via stdin, is converted to a SecureString immediately
      and never written anywhere.
    * An existing own account is corrected (attributes), its password is NEVER reset (X8).
    * A foreign account with the same sam/UPN/mail → needsInput (K4).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'AdHelpers.ps1'))

function Get-DesiredAttributes {
    param([Parameter(Mandatory)] [hashtable] $In)

    $id = $In['identity']
    $desired = [ordered]@{
        givenName         = [string] $id['givenName']
        sn                = [string] $id['sn']
        displayName       = [string] $id['displayName']
        mail              = [string] $id['mail']
        userPrincipalName = [string] $id['upn']
        department        = [string] $id['department']
        company           = [string] $id['company']
        scriptPath        = [string] $id['scriptPath']
    }
    if ($id['telephoneNumber']) {
        $desired['telephoneNumber'] = [string] $id['telephoneNumber']
    }
    foreach ($key in @($id['additionalAttributes'].Keys)) {
        $desired[$key] = [string] $id['additionalAttributes'][$key]
    }
    $desired[[string] $In['config']['requestIdAttribute']] = [string] $In['requestId']
    return $desired
}

function Invoke-AdCreateUser {
    param(
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] $Context
    )

    Import-ActiveDirectoryModule
    $id = $In['identity']
    $sam = [string] $id['sam']
    Assert-SamAccountName $sam
    if ([string]::IsNullOrWhiteSpace([string] $In['config']['requestIdAttribute'])) {
        return New-StepResult -Context $Context -Status failed -Code 'config-missing' -Reason 'RequestIdAttribute ist nicht konfiguriert.'
    }

    $desired = Get-DesiredAttributes -In $In
    $manager = Get-ADUser -Identity ([guid] $In['managerObjectGuid']) -ErrorAction Stop
    $properties = @($desired.Keys) + @('manager', 'proxyAddresses', 'objectGUID', 'distinguishedName')
    $existing = Find-AdUserBySam -Sam $sam -Properties $properties

    if ($null -ne $existing) {
        if (-not (Test-OwnAdAccount -User $existing -In $In)) {
            return New-StepResult -Context $Context -Status needsInput -Code 'foreign-account' `
                -Reason "Ein Konto mit sAMAccountName '$sam' existiert bereits und gehört nicht zu diesem Auftrag."
        }

        if (-not (Test-SameDn (Get-ParentDn ([string] $existing.DistinguishedName)) ([string] $id['ouDn']))) {
            return New-StepResult -Context $Context -Status needsInput -Code 'ou-mismatch' -Reason 'Das Konto des Auftrags liegt in einer anderen OU als konfiguriert.' `
                -DirectoryObjectGuid ([string] $existing.ObjectGUID)
        }

        # Idempotency: compare all attributes, not only existence (SPEC §7). Password untouched (X8).
        $replace = @{}
        foreach ($key in $desired.Keys) {
            if ([string] $existing.$key -cne [string] $desired[$key]) {
                $replace[$key] = $desired[$key]
            }
        }

        $wantedProxies = @($id['proxyAddresses'] | ForEach-Object { [string] $_ } | Sort-Object -CaseSensitive)
        $currentProxies = @($existing.proxyAddresses | ForEach-Object { [string] $_ } | Sort-Object -CaseSensitive)
        if (($wantedProxies -join "`n") -cne ($currentProxies -join "`n")) {
            $replace['proxyAddresses'] = [string[]] $wantedProxies
        }

        $managerDrift = [string] $existing.Manager -ne [string] $manager.DistinguishedName
        if ($replace.Count -eq 0 -and -not $managerDrift) {
            return New-StepResult -Context $Context -Status done -Reason 'Konto existiert bereits mit allen Attributen.' `
                -Output @{ objectGuid = [string] $existing.ObjectGUID } -DirectoryObjectGuid ([string] $existing.ObjectGUID)
        }

        $names = @($replace.Keys) + @(if ($managerDrift) { 'manager' })
        Invoke-StepChange -Context $Context -Description ("Attribute korrigieren: {0}" -f ($names -join ', ')) -Action {
            if ($replace.Count -gt 0) {
                Set-ADUser -Identity $existing.ObjectGUID -Replace $replace -ErrorAction Stop
            }
            if ($managerDrift) {
                Set-ADUser -Identity $existing.ObjectGUID -Manager $manager -ErrorAction Stop
            }
        } | Out-Null

        return New-StepResult -Context $Context -Status done -Reason ("Attribute korrigiert: {0}." -f ($names -join ', ')) `
            -Output @{ objectGuid = [string] $existing.ObjectGUID } -DirectoryObjectGuid ([string] $existing.ObjectGUID)
    }

    # UPN / mail / proxy address must not belong to another object.
    $mail = ConvertTo-LdapFilterValue ([string] $id['mail'])
    $upn = ConvertTo-LdapFilterValue ([string] $id['upn'])
    $clashFilter = "(|(userPrincipalName=$upn)(mail=$mail)(proxyAddresses=smtp:$mail))"
    if (Get-ADObject -LDAPFilter $clashFilter -ErrorAction Stop | Select-Object -First 1) {
        return New-StepResult -Context $Context -Status needsInput -Code 'address-in-use' -Reason 'UPN oder E-Mail-Adresse ist bereits einem anderen Objekt zugeordnet.'
    }

    # The CN (display name) must be unique within the OU.
    $cnFilter = '(cn={0})' -f (ConvertTo-LdapFilterValue ([string] $id['displayName']))
    if (Get-ADObject -LDAPFilter $cnFilter -SearchBase ([string] $id['ouDn']) -SearchScope OneLevel -ErrorAction Stop | Select-Object -First 1) {
        return New-StepResult -Context $Context -Status needsInput -Code 'cn-in-use' -Reason 'In der Ziel-OU existiert bereits ein Objekt mit diesem Anzeigenamen.'
    }

    if ([string]::IsNullOrEmpty([string] $In['initialPassword'])) {
        return New-StepResult -Context $Context -Status failed -Code 'missing-password' -Reason 'Kein Startpasswort vorhanden.'
    }

    $securePassword = ConvertTo-SecureString -String ([string] $In['initialPassword']) -AsPlainText -Force
    $In.Remove('initialPassword')

    $other = @{
        proxyAddresses = [string[]] @($id['proxyAddresses'])
    }
    foreach ($key in $desired.Keys) {
        if ($key -notin 'givenName', 'sn', 'displayName', 'mail', 'userPrincipalName', 'department', 'company', 'scriptPath', 'telephoneNumber') {
            $other[$key] = $desired[$key]
        }
    }

    $parameters = @{
        Name                  = [string] $id['displayName']
        SamAccountName        = $sam
        UserPrincipalName     = [string] $id['upn']
        GivenName             = [string] $id['givenName']
        Surname               = [string] $id['sn']
        DisplayName           = [string] $id['displayName']
        EmailAddress          = [string] $id['mail']
        Department            = [string] $id['department']
        Company               = [string] $id['company']
        ScriptPath            = [string] $id['scriptPath']
        Path                  = [string] $id['ouDn']
        Manager               = $manager
        AccountPassword       = $securePassword
        Enabled               = $false
        ChangePasswordAtLogon = $true
        OtherAttributes       = $other
        PassThru              = $true
        ErrorAction           = 'Stop'
    }
    if ($id['telephoneNumber']) {
        $parameters['OfficePhone'] = [string] $id['telephoneNumber']
    }

    $created = Invoke-StepChange -Context $Context `
        -Description ("Konto '{0}' deaktiviert anlegen in {1} (Kennwortänderung bei Anmeldung)" -f $sam, $id['ouDn']) `
        -Action { New-ADUser @parameters }

    if ($Context.DryRun) {
        return New-StepResult -Context $Context -Status done -Reason 'Dry-Run: Konto würde angelegt.'
    }

    return New-StepResult -Context $Context -Status done -Reason 'Konto deaktiviert angelegt, Kennwortänderung bei Anmeldung erzwungen.' `
        -Output @{ objectGuid = [string] $created.ObjectGUID; ou = [string] $id['ouDn'] } -DirectoryObjectGuid ([string] $created.ObjectGUID)
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-AdCreateUser}
}
