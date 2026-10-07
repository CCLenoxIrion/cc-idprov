#Requires -Version 7.2
<#
.SYNOPSIS
    EXO.WaitMailbox (SPEC §7): Waits until the mailbox exists in Exchange Online.
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Invoke-ExoWaitMailbox {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $upn = [string] (Get-CloudInput $In)['upn']
    Invoke-OnbCloudSession -Service Exchange -In $In -CommandName @('Get-EXOMailbox') -Body {
        if ($null -eq (Get-OnbMailbox -Identity $upn)) {
            return New-StepResult -Context $Context -Status waiting -Code 'mailbox-pending' -Reason 'Postfach noch nicht bereitgestellt.'
        }
        New-StepResult -Context $Context -Status done -Reason 'Postfach vorhanden.'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-ExoWaitMailbox}
}
