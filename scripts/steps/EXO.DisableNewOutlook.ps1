#Requires -Version 7.2
<#
.SYNOPSIS
    EXO.DisableNewOutlook (SPEC §7): Sets OneWinNativeOutlookEnabled as configured.
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Invoke-ExoDisableNewOutlook {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    $upn = [string] $cloud['upn']
    $desired = [bool] $cloud['oneWinNativeOutlookEnabled']
    Invoke-OnbCloudSession -Service Exchange -In $In -CommandName @('Get-EXOMailbox', 'Get-CASMailbox', 'Set-CASMailbox') -Body {
        if ($null -eq (Get-OnbMailbox -Identity $upn)) {
            return New-StepResult -Context $Context -Status failed -Code 'mailbox-not-found' -Reason 'Postfach nicht gefunden.'
        }

        $cas = Get-CASMailbox -Identity $upn -ErrorAction Stop
        if ([bool] $cas.OneWinNativeOutlookEnabled -eq $desired) {
            return New-StepResult -Context $Context -Status done -Reason ('OneWinNativeOutlookEnabled bereits {0}.' -f $desired)
        }

        Invoke-StepChange -Context $Context -Description ('OneWinNativeOutlookEnabled = {0} setzen' -f $desired) -Action {
            Set-CASMailbox -Identity $upn -OneWinNativeOutlookEnabled $desired -ErrorAction Stop | Out-Null
        } | Out-Null
        New-StepResult -Context $Context -Status done -Reason ('OneWinNativeOutlookEnabled = {0}.' -f $desired)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-ExoDisableNewOutlook}
}
