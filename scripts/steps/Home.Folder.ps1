#Requires -Version 7.2
<#
.SYNOPSIS
    Home.Folder (SPEC §7): calls New-OnbHomeFolder on the JEA endpoint of the home server (DECISIONS X5).
    Only the sAMAccountName is passed; paths, share names and ACLs are defined by the endpoint.
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force

function Invoke-HomeFolder {
    param(
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] $Context
    )

    $sam = [string] $In['identity']['sam']
    Assert-SamAccountName $sam
    $jea = $In['config']['jea']
    $result = Invoke-JeaFunction -ComputerName ([string] $jea['dcComputer']) -ConfigurationName ([string] $jea['dcConfigurationName']) `
        -FunctionName 'New-OnbHomeFolder' -Parameters @{ Sam = $sam; DryRun = [bool] $Context.DryRun }
    return ConvertFrom-JeaResult -Context $Context -JeaResult $result
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-HomeFolder}
}
