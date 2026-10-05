#Requires -Version 7.2
<#
.SYNOPSIS
    Teams.VoiceRouting (SPEC §7): Grants the online voice routing policy.
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Invoke-TeamsVoiceRouting {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    if (-not (Test-HasPhone $cloud)) {
        return New-StepResult -Context $Context -Status skipped -Code 'no-extension' -Reason 'Keine Durchwahl.'
    }

    $upn = [string] $cloud['upn']
    $policy = [string] $cloud['teams']['voiceRoutingPolicy']
    Invoke-OnbCloudSession -Service Teams -In $In -Body {
        $user = Get-OnbCsUser -Identity $upn
        if ($null -eq $user) {
            return New-StepResult -Context $Context -Status failed -Code 'teams-user-not-found' -Reason 'Teams-Benutzer nicht gefunden.'
        }

        if ([string] $user.OnlineVoiceRoutingPolicy -eq $policy) {
            return New-StepResult -Context $Context -Status done -Reason ("Voice-Routing-Policy '{0}' bereits gesetzt." -f $policy)
        }

        Invoke-StepChange -Context $Context -Description ("Voice-Routing-Policy '{0}' zuweisen" -f $policy) -Action {
            Grant-CsOnlineVoiceRoutingPolicy -Identity $upn -PolicyName $policy -ErrorAction Stop | Out-Null
        } | Out-Null
        New-StepResult -Context $Context -Status done -Reason ("Voice-Routing-Policy '{0}' zugewiesen." -f $policy)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-TeamsVoiceRouting}
}
