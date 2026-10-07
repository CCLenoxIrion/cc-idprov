<#
.SYNOPSIS
    Shared logic of Sync.Delta and Sync.DeltaAfterEnable: calls the parameterless
    Start-OnbDeltaSync on the JEA endpoint of the Entra Connect server (DECISIONS X5).
#>

function Invoke-DeltaSync {
    param(
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] $Context
    )

    $jea = $In['config']['jea']
    $server = [string] $jea['syncComputer']
    if ($Context.DryRun) {
        $Context.PlannedActions.Add(("Delta-Sync auf {0} auslösen" -f $server))
        return New-StepResult -Context $Context -Status done -Reason 'Dry-Run: Delta-Sync würde ausgelöst.'
    }

    $result = Invoke-JeaFunction -ComputerName $server -ConfigurationName ([string] $jea['syncConfigurationName']) -FunctionName 'Start-OnbDeltaSync'
    return ConvertFrom-JeaResult -Context $Context -JeaResult $result
}
