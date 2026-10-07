#Requires -Version 7.2
<#
.SYNOPSIS
    Home.Share (SPEC §7): calls New-OnbHomeShare on the JEA endpoint of the home server (DECISIONS X5).
    Only the sAMAccountName is passed; paths, share names and ACLs are defined by the endpoint.
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force

function Invoke-HomeShare {
    param(
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] $Context
    )

    $sam = [string] $In['identity']['sam']
    Assert-SamAccountName $sam
    $jea = $In['config']['jea']
    $result = Invoke-JeaFunction -ComputerName ([string] $jea['homeComputer']) -ConfigurationName ([string] $jea['homeConfigurationName']) `
        -FunctionName 'New-OnbHomeShare' -Parameters @{ Sam = $sam; DryRun = [bool] $Context.DryRun }
    return ConvertFrom-JeaResult -Context $Context -JeaResult $result
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-HomeShare}
}
