#Requires -Version 7.2
<#
.SYNOPSIS
    Home.Folder (SPEC §7): calls New-OnbHomeFolder on the JEA endpoint of the file server
    (DECISIONS X5, X17). Passed: sam, user right and additional ACEs ('Principal=Right'); the
    endpoint accepts only rights from its allowlist and knows all paths itself.
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
    $homeAcl = $In['config']['home']
    if ($null -eq $homeAcl -or [string]::IsNullOrEmpty([string] $homeAcl['userRight'])) {
        throw (New-SafeException -Code 'config-missing' 'NTFS-Rechte für den Home-Ordner fehlen in der Eingabe.')
    }

    $aces = @($homeAcl['additionalAces'] | Where-Object { $null -ne $_ } | ForEach-Object { '{0}={1}' -f $_['principal'], $_['right'] })
    $result = Invoke-JeaFunction -ComputerName ([string] $jea['homeComputer']) -ConfigurationName ([string] $jea['homeConfigurationName']) `
        -FunctionName 'New-OnbHomeFolder' -Parameters @{
            Sam            = $sam
            UserRight      = [string] $homeAcl['userRight']
            AdditionalAces = [string[]] $aces
            Force          = [bool] $In['force']
            DryRun         = [bool] $Context.DryRun
        }
    return ConvertFrom-JeaResult -Context $Context -JeaResult $result
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-HomeFolder}
}
