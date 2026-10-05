#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5.0' }
<# Tests of the step scripts that call JEA endpoints (Invoke-JeaFunction mocked). #>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Stubs.ps1')
    . (Join-Path $PSScriptRoot '..' 'steps' 'Home.Folder.ps1')
    . (Join-Path $PSScriptRoot '..' 'steps' 'Home.Share.ps1')
    . (Join-Path $PSScriptRoot '..' 'steps' 'Logon.Script.ps1')
    . (Join-Path $PSScriptRoot '..' 'steps' 'Sync.Delta.ps1')
}

Describe 'Home.Folder / Home.Share' {
    It 'passes only the sam (no paths) to <Function>' -TestCases @(
        @{ Function = 'New-OnbHomeFolder'; Handler = 'Invoke-HomeFolder' }
        @{ Function = 'New-OnbHomeShare'; Handler = 'Invoke-HomeShare' }
    ) {
        Mock Invoke-JeaFunction { [pscustomobject]@{ status = 'done'; reason = 'ok'; plannedActions = @(); output = [pscustomobject]@{} } }
        $result = & $Handler (New-TestStepInput -Step 'Home.Folder') (New-StepContext -DryRun $false)

        $result.status | Should -Be 'done'
        Should -Invoke Invoke-JeaFunction -Times 1 -Exactly -ParameterFilter {
            $FunctionName -eq $Function -and $ComputerName -eq 'DC01' -and $ConfigurationName -eq 'CC.Onboarding' -and
            @($Parameters.Keys | Sort-Object) -join ',' -eq 'DryRun,Sam' -and $Parameters['Sam'] -eq 'lirion'
        }
    }

    It 'dry-run is forwarded and planned actions are returned' {
        Mock Invoke-JeaFunction { [pscustomobject]@{ status = 'done'; reason = 'Dry-Run.'; plannedActions = @('Ordner anlegen'); output = $null } }
        $result = Invoke-HomeFolder (New-TestStepInput -Step 'Home.Folder' -DryRun) (New-StepContext -DryRun $true)
        $result.plannedActions | Should -Be @('Ordner anlegen')
        Should -Invoke Invoke-JeaFunction -ParameterFilter { $Parameters['DryRun'] -eq $true }
    }

    It 'invalid sam is rejected before any remote call' {
        Mock Invoke-JeaFunction { throw 'should not be called' }
        $in = New-TestStepInput -Step 'Home.Folder'
        $in['identity']['sam'] = '..\admin'
        { Invoke-HomeFolder $in (New-StepContext -DryRun $false) } | Should -Throw
        Should -Invoke Invoke-JeaFunction -Times 0 -Exactly
    }

    It 'transport errors become a sanitized reason' {
        Mock Invoke-JeaFunction { throw [System.Management.Automation.Remoting.PSRemotingTransportException]::new('Zugriff verweigert für DC01\geheim') }
        $context = New-StepContext -DryRun $false
        $result = try { Invoke-HomeShare (New-TestStepInput -Step 'Home.Share') $context } catch { New-StepResult -Context $context -Status failed -Reason (Get-SafeErrorReason $_) }
        $result.reason | Should -BeExactly 'JEA-Endpunkt nicht erreichbar oder Zugriff verweigert.'
    }
}

Describe 'Logon.Script' {
    It 'forwards content, hash, force and dry-run' {
        Mock Invoke-JeaFunction { [pscustomobject]@{ status = 'needsInput'; reason = 'manuell geändert'; plannedActions = @(); output = $null } }
        $in = New-TestStepInput -Step 'Logon.Script' -Force
        $in['logonScript']['sha256'] = 'a' * 64
        $result = Invoke-LogonScript $in (New-StepContext -DryRun $false)

        $result.status | Should -Be 'needsInput'
        Should -Invoke Invoke-JeaFunction -ParameterFilter {
            $FunctionName -eq 'Set-OnbLogonScript' -and $Parameters['Force'] -eq $true -and $Parameters['Sha256'] -eq ('a' * 64) -and
            -not $Parameters.ContainsKey('Path')
        }
    }

    It 'missing content → failed before the remote call' {
        Mock Invoke-JeaFunction { throw 'should not be called' }
        $in = New-TestStepInput -Step 'Logon.Script'
        $in['logonScript'] = $null
        { Invoke-LogonScript $in (New-StepContext -DryRun $false) } | Should -Throw -ExpectedMessage 'Inhalt des Anmeldeskripts fehlt in der Eingabe.'
    }
}

Describe 'Sync.Delta' {
    It 'calls the parameterless function on the sync endpoint' {
        Mock Invoke-JeaFunction { [pscustomobject]@{ status = 'waiting'; reason = 'busy'; plannedActions = @(); output = $null } }
        $result = Invoke-DeltaSync (New-TestStepInput -Step 'Sync.Delta') (New-StepContext -DryRun $false)

        $result.status | Should -Be 'waiting'
        Should -Invoke Invoke-JeaFunction -ParameterFilter {
            $FunctionName -eq 'Start-OnbDeltaSync' -and $ComputerName -eq 'CC01' -and $ConfigurationName -eq 'CC.Onboarding.Sync' -and $Parameters.Count -eq 0
        }
    }

    It 'dry-run does not call the endpoint' {
        Mock Invoke-JeaFunction { throw 'should not be called' }
        $result = Invoke-DeltaSync (New-TestStepInput -Step 'Sync.Delta' -DryRun) (New-StepContext -DryRun $true)
        $result.plannedActions | Should -Be @('Delta-Sync auf CC01 auslösen')
    }
}
