#Requires -Version 7.2
<#
.SYNOPSIS
    Sync.Delta (SPEC §7, DECISIONS S9): triggers a delta sync on the Entra Connect server via JEA.
    "busy" is returned as waiting (retry with backoff).
#>
[CmdletBinding()]
param()

Import-Module (Join-Path $PSScriptRoot '..' 'common' 'Onboarding.Step.psm1') -Force
. (Join-Path $PSScriptRoot '..' 'common' 'SyncHelpers.ps1')

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-DeltaSync}
}
