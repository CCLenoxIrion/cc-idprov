#Requires -Version 7.2
<#
.SYNOPSIS
    AD.Enable (SPEC §7): enables the request's own account on the entry date.
#>
[CmdletBinding()]
param()

Import-Module (Join-Path $PSScriptRoot '..' 'common' 'Onboarding.Step.psm1') -Force
. (Join-Path $PSScriptRoot '..' 'common' 'AdHelpers.ps1')

function Invoke-AdEnable {
    param(
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] $Context
    )

    Import-ActiveDirectoryModule
    $own = Get-OwnAdAccount -In $In -Context $Context -Properties @('enabled', 'objectGUID')
    if ($own.ContainsKey('Result')) { return $own.Result }
    $user = $own.User

    if ($user.Enabled) {
        return New-StepResult -Context $Context -Status done -Reason 'Konto bereits aktiviert.'
    }

    Invoke-StepChange -Context $Context -Description ("Konto '{0}' aktivieren" -f $In['identity']['sam']) -Action {
        Enable-ADAccount -Identity $user.ObjectGUID -ErrorAction Stop
    } | Out-Null

    return New-StepResult -Context $Context -Status done -Reason 'Konto aktiviert.'
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-AdEnable}
}
