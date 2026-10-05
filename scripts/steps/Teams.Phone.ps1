#Requires -Version 7.2
<#
.SYNOPSIS
    Teams.Phone (SPEC §7): Assigns the phone number (Set-CsPhoneNumberAssignment).
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function ConvertFrom-LineUri {
    param([AllowNull()] [AllowEmptyString()] [string] $LineUri)
    if ([string]::IsNullOrWhiteSpace($LineUri)) { return '' }
    return ($LineUri -replace '^tel:', '' -split ';')[0]
}

function Invoke-TeamsPhone {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    if (-not (Test-HasPhone $cloud)) {
        return New-StepResult -Context $Context -Status skipped -Code 'no-extension' -Reason 'Keine Durchwahl.'
    }

    $upn = [string] $cloud['upn']
    $number = [string] $cloud['phoneE164']
    $type = [string] $cloud['teams']['phoneNumberType']
    Invoke-OnbCloudSession -Service Teams -In $In -Body {
        $user = Get-OnbCsUser -Identity $upn
        if ($null -eq $user) {
            return New-StepResult -Context $Context -Status failed -Code 'teams-user-not-found' -Reason 'Teams-Benutzer nicht gefunden.'
        }

        if ((ConvertFrom-LineUri ([string] $user.LineUri)) -eq $number) {
            return New-StepResult -Context $Context -Status done -Reason ('Nummer {0} bereits zugewiesen.' -f $number)
        }

        $assignment = Get-CsPhoneNumberAssignment -TelephoneNumber $number -ErrorAction Stop | Select-Object -First 1
        if ($null -ne $assignment -and -not [string]::IsNullOrEmpty([string] $assignment.AssignedPstnTargetId) -and
            [string] $assignment.AssignedPstnTargetId -ne [string] $user.Identity) {
            return New-StepResult -Context $Context -Status failed -Code 'number-in-use' -Reason ('Nummer {0} ist bereits einem anderen Benutzer zugewiesen.' -f $number)
        }

        Invoke-StepChange -Context $Context -Description ('Nummer {0} ({1}) zuweisen' -f $number, $type) -Action {
            Set-CsPhoneNumberAssignment -Identity $upn -PhoneNumber $number -PhoneNumberType $type -ErrorAction Stop | Out-Null
        } | Out-Null
        New-StepResult -Context $Context -Status done -Reason ('Nummer {0} ({1}) zugewiesen.' -f $number, $type)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-TeamsPhone}
}
