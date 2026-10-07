#Requires -Version 7.2
<#
.SYNOPSIS
    AD.Groups (SPEC §7): adds the request's account to the department's AD groups.
    Idempotent: existing memberships are left alone.
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'AdHelpers.ps1'))

function Invoke-AdGroups {
    param(
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] $Context
    )

    Import-ActiveDirectoryModule
    $own = Get-OwnAdAccount -In $In -Context $Context -Properties @('memberOf', 'objectGUID')
    if ($own.ContainsKey('Result')) { return $own.Result }
    $user = $own.User
    $memberOf = @($user.memberOf | ForEach-Object { [string] $_ })

    $added = [System.Collections.Generic.List[string]]::new()
    foreach ($groupName in @($In['config']['groups'])) {
        if ([string]::IsNullOrWhiteSpace([string] $groupName)) { continue }
        try {
            $group = Get-ADGroup -Identity ([string] $groupName) -ErrorAction Stop
        }
        catch [Microsoft.ActiveDirectory.Management.ADIdentityNotFoundException] {
            throw (New-SafeException -Code 'group-not-found' ("Gruppe '{0}' nicht gefunden." -f $groupName))
        }

        if ($memberOf -contains [string] $group.DistinguishedName) { continue }

        Invoke-StepChange -Context $Context -Description ("Mitglied in Gruppe '{0}'" -f $groupName) -Action {
            Add-ADGroupMember -Identity $group -Members $user -ErrorAction Stop
        } | Out-Null
        $added.Add([string] $groupName)
    }

    $reason = if ($added.Count -eq 0) { 'Alle Gruppen bereits zugewiesen.' } else { 'Hinzugefügt: {0}.' -f ($added -join ', ') }
    return New-StepResult -Context $Context -Status done -Reason $reason -Output @{ groups = @($In['config']['groups']) }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-AdGroups}
}
