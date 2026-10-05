#Requires -Version 7.2
<#
.SYNOPSIS
    Teams.Voicemail (SPEC §7): Voicemail policy and settings; ManualTask with ready command if configured (X13).
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Get-VoicemailCommand {
    param([string] $Upn, [string] $Policy, [string] $Language)
    return @(
        'Connect-MicrosoftTeams'
        ('Grant-CsOnlineVoicemailPolicy -Identity {0} -PolicyName {1}' -f (ConvertTo-PsLiteral $Upn), (ConvertTo-PsLiteral $Policy))
        ('Set-CsOnlineVoicemailUserSettings -Identity {0} -VoicemailEnabled $true -PromptLanguage {1} -DefaultGreetingPromptOverwrite ""' -f (ConvertTo-PsLiteral $Upn), (ConvertTo-PsLiteral $Language))
    )
}

function Invoke-TeamsVoicemail {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    if (-not (Test-HasPhone $cloud)) {
        return New-StepResult -Context $Context -Status skipped -Code 'no-extension' -Reason 'Keine Durchwahl.'
    }
    if (-not [bool] $cloud['teams']['voicemail']) {
        return New-StepResult -Context $Context -Status skipped -Code 'voicemail-disabled' -Reason 'Voicemail ist für die Abteilung nicht vorgesehen.'
    }

    $upn = [string] $cloud['upn']
    $policy = [string] $cloud['teams']['voicemailPolicy']
    $language = [string] $cloud['teams']['promptLanguage']
    if ([bool] $cloud['manualOnly']) {
        # No connection at all: the admin runs the commands (app-only support to verify, SPEC §3).
        return New-StepResult -Context $Context -Status manualTask -Code 'manual-step' `
            -Reason ('Manuell ausführen: ' + ((Get-VoicemailCommand -Upn $upn -Policy $policy -Language $language) -join '; '))
    }

    Invoke-OnbCloudSession -Service Teams -In $In -Body {
        $user = Get-OnbCsUser -Identity $upn
        if ($null -eq $user) {
            return New-StepResult -Context $Context -Status failed -Code 'teams-user-not-found' -Reason 'Teams-Benutzer nicht gefunden.'
        }

        $changes = [System.Collections.Generic.List[string]]::new()
        if ([string] $user.OnlineVoicemailPolicy -ne $policy) {
            Invoke-StepChange -Context $Context -Description ("Voicemail-Policy '{0}' zuweisen" -f $policy) -Action {
                Grant-CsOnlineVoicemailPolicy -Identity $upn -PolicyName $policy -ErrorAction Stop | Out-Null
            } | Out-Null
            $changes.Add('Policy')
        }

        $settings = Get-CsOnlineVoicemailUserSettings -Identity $upn -ErrorAction Stop
        if (-not [bool] $settings.VoicemailEnabled -or [string] $settings.PromptLanguage -ne $language) {
            Invoke-StepChange -Context $Context -Description ('Voicemail aktivieren, Sprache {0}' -f $language) -Action {
                Set-CsOnlineVoicemailUserSettings -Identity $upn -VoicemailEnabled $true -PromptLanguage $language -DefaultGreetingPromptOverwrite '' -ErrorAction Stop | Out-Null
            } | Out-Null
            $changes.Add('Einstellungen')
        }

        $reason = if ($changes.Count -eq 0) { 'Voicemail bereits eingerichtet.' } else { 'Voicemail eingerichtet ({0}).' -f ($changes -join ', ') }
        New-StepResult -Context $Context -Status done -Reason $reason
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-TeamsVoicemail}
}
