#Requires -Version 7.2
<#
.SYNOPSIS
    Entra.UsageLocation (SPEC §7): Sets usageLocation (required before any license assignment).
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Invoke-EntraUsageLocation {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    $desired = [string] $cloud['usageLocation']
    Invoke-OnbCloudSession -Service Graph -In $In -Body {
        $own = Get-OwnCloudUser -In $In -Context $Context
        if ($own.ContainsKey('Result')) { return $own.Result }
        if ([string] $own.User['usageLocation'] -ceq $desired) {
            return New-StepResult -Context $Context -Status done -Reason ('usageLocation bereits {0}.' -f $desired)
        }

        $id = [string] $own.User['id']
        Invoke-StepChange -Context $Context -Description ('usageLocation auf {0} setzen' -f $desired) -Action {
            Invoke-OnbGraph -Method PATCH -Uri ('v1.0/users/{0}' -f [uri]::EscapeDataString($id)) -Body @{ usageLocation = $desired } | Out-Null
        } | Out-Null
        New-StepResult -Context $Context -Status done -Reason ('usageLocation = {0}.' -f $desired)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-EntraUsageLocation}
}
