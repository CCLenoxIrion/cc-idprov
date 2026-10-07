<#
.SYNOPSIS
    Discovery guard for the test files of the PowerShell 7 step scripts. Dot-sourced in
    BeforeDiscovery. Must itself stay Windows PowerShell 5.1 compatible.

    The step scripts carry #Requires -Version 7.2. Under Windows PowerShell 5.1 the tests of
    those files are skipped with a clear warning instead of failing the whole container with a
    ScriptRequiresException. Under 5.1 only JeaEndpoint.Tests.ps1 is meant to run.
#>

$SkipUnlessPwsh7 = $PSVersionTable.PSVersion.Major -lt 7
if ($SkipUnlessPwsh7) {
    Write-Warning ('Diese Tests laufen nur unter PowerShell 7 (pwsh), aktuell {0}. Unter Windows PowerShell 5.1 nur JeaEndpoint.Tests.ps1 ausführen (DEPLOYMENT.md §9).' -f $PSVersionTable.PSVersion)
}
