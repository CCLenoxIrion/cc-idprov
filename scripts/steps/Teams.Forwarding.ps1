#Requires -Version 7.2
<#
.SYNOPSIS
    Teams.Forwarding (SPEC §7): Unanswered-call forwarding (Set-CsUserCallingSettings); ManualTask if configured (X13).
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Get-ForwardingCommand {
    param([string] $Upn, [string] $Delay, [string] $TargetType, [string] $Target)
    return @(
        'Connect-MicrosoftTeams'
        ('Set-CsUserCallingSettings -Identity {0} -IsUnansweredEnabled $true -UnansweredDelay {1} -UnansweredTargetType {2} -UnansweredTarget {3}' -f `
                (ConvertTo-PsLiteral $Upn), (ConvertTo-PsLiteral $Delay), (ConvertTo-PsLiteral $TargetType), (ConvertTo-PsLiteral $Target))
    )
}

function ConvertTo-PlainTarget {
    param([AllowNull()] [AllowEmptyString()] [string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ($Value -replace '^(sip|tel):', '').ToLowerInvariant()
}

function Invoke-TeamsForwarding {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    if (-not (Test-HasPhone $cloud)) {
        return New-StepResult -Context $Context -Status skipped -Code 'no-extension' -Reason 'Keine Durchwahl.'
    }
    $forward = $cloud['teams']['forward']
    if (-not [bool] $forward['enabled']) {
        return New-StepResult -Context $Context -Status skipped -Code 'forwarding-disabled' -Reason 'Weiterleitung ist für die Abteilung nicht vorgesehen.'
    }

    $upn = [string] $cloud['upn']
    $delay = [timespan]::FromSeconds([int] $forward['delaySeconds']).ToString('hh\:mm\:ss')
    $targetType = [string] $forward['targetType']
    $target = [string] $forward['target']
    if ([bool] $cloud['manualOnly']) {
        return New-StepResult -Context $Context -Status manualTask -Code 'manual-step' `
            -Reason ('Manuell ausführen: ' + ((Get-ForwardingCommand -Upn $upn -Delay $delay -TargetType $targetType -Target $target) -join '; '))
    }

    Invoke-OnbCloudSession -Service Teams -In $In -Body {
        $settings = Invoke-OnbTeams -Command Get-CsUserCallingSettings -Parameters @{ Identity = $upn; ErrorAction = 'Stop' }
        $current = [bool] $settings.IsUnansweredEnabled -and
            [string] $settings.UnansweredDelay -eq $delay -and
            [string] $settings.UnansweredTargetType -eq $targetType -and
            (ConvertTo-PlainTarget ([string] $settings.UnansweredTarget)) -eq (ConvertTo-PlainTarget $target)
        if ($current) {
            return New-StepResult -Context $Context -Status done -Reason 'Weiterleitung bereits eingerichtet.'
        }

        Invoke-StepChange -Context $Context -Description ('Weiterleitung bei Nichtannahme nach {0} an {1}' -f $delay, $target) -Action {
            Invoke-OnbTeams -Command Set-CsUserCallingSettings -Parameters @{
                Identity = $upn; IsUnansweredEnabled = $true; UnansweredDelay = $delay; UnansweredTargetType = $targetType; UnansweredTarget = $target; ErrorAction = 'Stop'
            } | Out-Null
        } | Out-Null
        New-StepResult -Context $Context -Status done -Reason ('Weiterleitung nach {0} an {1} eingerichtet.' -f $delay, $target)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-TeamsForwarding}
}
