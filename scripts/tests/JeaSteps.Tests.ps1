#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5.0' }
<# Tests of the step scripts that call JEA endpoints (Invoke-JeaFunction mocked). #>

BeforeDiscovery {
    . (Join-Path $PSScriptRoot 'TestEnv.ps1')
}

BeforeAll {
    if ($PSVersionTable.PSVersion.Major -lt 7) { return }
    . (Join-Path $PSScriptRoot 'Stubs.ps1')
    . ([System.IO.Path]::Combine($PSScriptRoot, '..', 'steps', 'Home.Folder.ps1'))
    . ([System.IO.Path]::Combine($PSScriptRoot, '..', 'steps', 'Home.Share.ps1'))
    . ([System.IO.Path]::Combine($PSScriptRoot, '..', 'steps', 'Logon.Script.ps1'))
    . ([System.IO.Path]::Combine($PSScriptRoot, '..', 'steps', 'Sync.Delta.ps1'))
}

Describe 'Home.Folder / Home.Share' -Skip:$SkipUnlessPwsh7 {
    It 'Home.Share passes only the sam (no paths) to the file server endpoint' {
        Mock Invoke-JeaFunction { [pscustomobject]@{ status = 'done'; reason = 'ok'; plannedActions = @(); output = [pscustomobject]@{} } }
        $result = Invoke-HomeShare (New-TestStepInput -Step 'Home.Share') (New-StepContext -DryRun $false)

        $result.status | Should -Be 'done'
        Should -Invoke Invoke-JeaFunction -Times 1 -Exactly -ParameterFilter {
            $FunctionName -eq 'New-OnbHomeShare' -and $ComputerName -eq 'DC01' -and $ConfigurationName -eq 'CC.Onboarding' -and
            @($Parameters.Keys | Sort-Object) -join ',' -eq 'DryRun,Sam' -and $Parameters['Sam'] -eq 'lirion'
        }
    }

    It 'Home.Folder passes sam, user right and ACEs as Principal=Right, no paths' {
        Mock Invoke-JeaFunction { [pscustomobject]@{ status = 'done'; reason = 'ok'; plannedActions = @(); output = [pscustomobject]@{} } }
        $in = New-TestStepInput -Step 'Home.Folder' -Force
        $in['config']['home']['userRight'] = 'FullControl'
        $result = Invoke-HomeFolder $in (New-StepContext -DryRun $false)

        $result.status | Should -Be 'done'
        Should -Invoke Invoke-JeaFunction -Times 1 -Exactly -ParameterFilter {
            $FunctionName -eq 'New-OnbHomeFolder' -and $ComputerName -eq 'DC01' -and $ConfigurationName -eq 'CC.Onboarding' -and
            @($Parameters.Keys | Sort-Object) -join ',' -eq 'AdditionalAces,DryRun,Force,Sam,UserRight' -and
            $Parameters['UserRight'] -eq 'FullControl' -and $Parameters['Force'] -eq $true -and
            (@($Parameters['AdditionalAces']) -join '|') -eq 'SYSTEM=FullControl|BUILTIN\Administrators=FullControl'
        }
    }

    It 'Home.Folder without home ACL configuration → failed config-missing before the remote call' {
        Mock Invoke-JeaFunction { throw 'should not be called' }
        $in = New-TestStepInput -Step 'Home.Folder'
        $in['config']['home'] = $null
        $result = Invoke-StepHandler ${function:Invoke-HomeFolder} $in (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'config-missing'
        Should -Invoke Invoke-JeaFunction -Times 0 -Exactly
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
        $result = Invoke-StepHandler ${function:Invoke-HomeFolder} $in (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'invalid-sam'
        Should -Invoke Invoke-JeaFunction -Times 0 -Exactly
    }

    It 'transport errors become a sanitized reason' {
        Mock Invoke-JeaFunction { throw [System.Management.Automation.Remoting.PSRemotingTransportException]::new('Zugriff verweigert für DC01\geheim') }
        $result = Invoke-StepHandler ${function:Invoke-HomeShare} (New-TestStepInput -Step 'Home.Share') (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'jea-unreachable'
        $result.reason | Should -BeExactly 'JEA-Endpunkt nicht erreichbar oder Zugriff verweigert.'
    }

    It 'passes the endpoint code through' {
        Mock Invoke-JeaFunction { [pscustomobject]@{ status = 'needsInput'; code = 'share-path-mismatch'; reason = 'anderer Pfad'; plannedActions = @(); output = $null } }
        $result = Invoke-HomeShare (New-TestStepInput -Step 'Home.Share') (New-StepContext -DryRun $false)
        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'share-path-mismatch'
    }
}

Describe 'Logon.Script' -Skip:$SkipUnlessPwsh7 {
    It 'forwards content, hash, force and dry-run' {
        Mock Invoke-JeaFunction { [pscustomobject]@{ status = 'needsInput'; code = 'logon-script-modified'; reason = 'manuell geändert'; plannedActions = @(); output = $null } }
        $in = New-TestStepInput -Step 'Logon.Script' -Force
        $in['logonScript']['sha256'] = 'a' * 64
        $result = Invoke-LogonScript $in (New-StepContext -DryRun $false)

        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'logon-script-modified'
        Should -Invoke Invoke-JeaFunction -ParameterFilter {
            $FunctionName -eq 'Set-OnbLogonScript' -and $ComputerName -eq 'DC03' -and $ConfigurationName -eq 'CC.Onboarding.Logon' -and
            $Parameters['Force'] -eq $true -and $Parameters['Sha256'] -eq ('a' * 64) -and
            -not $Parameters.ContainsKey('Path')
        }
    }

    It 'missing content → failed before the remote call' {
        Mock Invoke-JeaFunction { throw 'should not be called' }
        $in = New-TestStepInput -Step 'Logon.Script'
        $in['logonScript'] = $null
        $result = Invoke-StepHandler ${function:Invoke-LogonScript} $in (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'missing-logon-content'
        $result.reason | Should -Be 'Inhalt des Anmeldeskripts fehlt in der Eingabe.'
        Should -Invoke Invoke-JeaFunction -Times 0 -Exactly
    }
}

Describe 'Sync.Delta' -Skip:$SkipUnlessPwsh7 {
    It 'calls the parameterless function on the sync endpoint' {
        Mock Invoke-JeaFunction { [pscustomobject]@{ status = 'waiting'; code = 'sync-busy'; reason = 'busy'; plannedActions = @(); output = $null } }
        $result = Invoke-DeltaSync (New-TestStepInput -Step 'Sync.Delta') (New-StepContext -DryRun $false)

        $result.status | Should -Be 'waiting'
        $result.code | Should -Be 'sync-busy'
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
