#Requires -Version 7.2
<#
.SYNOPSIS
    Entra.WaitUser (SPEC §7): Waits until the synced user is visible in Entra and belongs to the request (X14).
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Invoke-EntraWaitUser {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    Invoke-OnbCloudSession -Service Graph -In $In -Body {
        $own = Get-OwnCloudUser -In $In -Context $Context -MissingStatus waiting
        if ($own.ContainsKey('Result')) { return $own.Result }
        New-StepResult -Context $Context -Status done -Reason 'Benutzer in Entra gefunden.' -Output @{ entraObjectId = [string] $own.User['id'] }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-EntraWaitUser}
}
