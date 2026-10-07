#Requires -Version 7.2
<#
.SYNOPSIS
    Teams.WaitUser (SPEC §7): Waits until the Teams user exists with Phone System (can take hours).
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Invoke-TeamsWaitUser {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    if (-not (Test-HasPhone $cloud)) {
        return New-StepResult -Context $Context -Status skipped -Code 'no-extension' -Reason 'Keine Durchwahl.'
    }

    $upn = [string] $cloud['upn']
    Invoke-OnbCloudSession -Service Teams -In $In -Body {
        $user = Get-OnbCsUser -Identity $upn
        # ZU VERIFIZIEREN: FeatureTypes enthält 'PhoneSystem', sobald MCOEV wirksam ist.
        if ($null -eq $user -or @($user.FeatureTypes) -notcontains 'PhoneSystem') {
            return New-StepResult -Context $Context -Status waiting -Code 'teams-user-pending' -Reason 'Teams-Benutzer mit Phone System noch nicht verfügbar.'
        }
        New-StepResult -Context $Context -Status done -Reason 'Teams-Benutzer mit Phone System verfügbar.'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-TeamsWaitUser}
}
