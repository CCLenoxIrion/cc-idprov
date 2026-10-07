#Requires -Version 7.2
<#
.SYNOPSIS
    Entra.WaitEnabled (SPEC §7): Waits until accountEnabled = true in Entra (DECISIONS S9).
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Invoke-EntraWaitEnabled {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    Invoke-OnbCloudSession -Service Graph -In $In -Body {
        $own = Get-OwnCloudUser -In $In -Context $Context
        if ($own.ContainsKey('Result')) { return $own.Result }
        if ($own.User['accountEnabled'] -eq $true) {
            return New-StepResult -Context $Context -Status done -Reason 'accountEnabled = true in Entra.'
        }
        New-StepResult -Context $Context -Status waiting -Code 'entra-enable-pending' -Reason 'accountEnabled in Entra noch false (Synchronisierung abwarten).'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-EntraWaitEnabled}
}
