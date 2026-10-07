#Requires -Version 7.2
<#
.SYNOPSIS
    Logon.Script (SPEC §4.4, §7): writes the logon script via Set-OnbLogonScript on the JEA
    endpoint "CC.Onboarding.Logon" of the domain controller GlobalConfig.LogonScript.Server
    (local NETLOGON, replicated by DFSR).

.NOTES
    The bytes come from the C# generator (byte-identical to the UI preview, AK 3) together with
    their SHA-256. The endpoint checks hash, ASCII and CRLF and is idempotent: same hash → done,
    other content without force → needsInput (DECISIONS L3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force

function Invoke-LogonScript {
    param(
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] $Context
    )

    $sam = [string] $In['identity']['sam']
    Assert-SamAccountName $sam
    $logon = $In['logonScript']
    if ($null -eq $logon -or -not $logon['contentBase64'] -or -not $logon['sha256']) {
        throw (New-SafeException -Code 'missing-logon-content' 'Inhalt des Anmeldeskripts fehlt in der Eingabe.')
    }

    $jea = $In['config']['jea']
    $result = Invoke-JeaFunction -ComputerName ([string] $jea['logonComputer']) -ConfigurationName ([string] $jea['logonConfigurationName']) `
        -FunctionName 'Set-OnbLogonScript' -Parameters @{
            Sam           = $sam
            ContentBase64 = [string] $logon['contentBase64']
            Sha256        = [string] $logon['sha256']
            Force         = [bool] $In['force']
            DryRun        = [bool] $Context.DryRun
        }
    return ConvertFrom-JeaResult -Context $Context -JeaResult $result
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-LogonScript}
}
