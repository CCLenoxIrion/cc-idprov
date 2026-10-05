#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5.0' }
<# Tests of the shared module Onboarding.Step.psm1. Run: Invoke-Pester ./scripts/tests #>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Stubs.ps1')
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'Onboarding.Step.psm1') -Force
}

Describe 'ConvertTo-LdapFilterValue (RFC 4515)' {
    It 'escapes <Value>' -TestCases @(
        @{ Value = 'lirion'; Expected = 'lirion' }
        @{ Value = '*'; Expected = '\2a' }
        @{ Value = 'a*)(objectClass=*'; Expected = 'a\2a\29\28objectClass=\2a' }
        @{ Value = 'back\slash'; Expected = 'back\5cslash' }
        @{ Value = "nul`0"; Expected = 'nul\00' }
        @{ Value = ''; Expected = '' }
    ) {
        ConvertTo-LdapFilterValue $Value | Should -BeExactly $Expected
    }
}

Describe 'Assert-SamAccountName' {
    It 'accepts <Sam>' -TestCases @(@{ Sam = 'lirion' }, @{ Sam = 'a1' }, @{ Sam = 'abcdefghijklmnopqrst' }) {
        { Assert-SamAccountName $Sam } | Should -Not -Throw
    }

    It 'rejects <Sam>' -TestCases @(
        @{ Sam = '' }, @{ Sam = 'L.Irion' }, @{ Sam = 'lirion*' }, @{ Sam = '..\x' }, @{ Sam = 'abcdefghijklmnopqrstu' }, @{ Sam = 'müller' }
    ) {
        { Assert-SamAccountName $Sam } | Should -Throw
    }
}

Describe 'Get-SafeErrorReason' {
    It 'never returns raw exception messages' {
        $raw = [System.Exception]::new('Kennwort Geheim-Start!2026 für CN=Lenox Irion abgelehnt')
        $reason = Get-SafeErrorReason $raw
        $reason | Should -Not -Match 'Geheim'
        $reason | Should -Not -Match 'Lenox'
        $reason | Should -BeExactly 'Unerwarteter Fehler (Exception).'
    }

    It 'maps AD exceptions to fixed texts' {
        Get-SafeErrorReason ([Microsoft.ActiveDirectory.Management.ADPasswordComplexityException]::new('Geheim-Start!2026')) |
            Should -BeExactly 'Startpasswort entspricht nicht der Domänen-Kennwortrichtlinie.'
    }

    It 'returns messages of safe exceptions' {
        Get-SafeErrorReason (New-SafeException 'Gruppe X nicht gefunden.') | Should -BeExactly 'Gruppe X nicht gefunden.'
    }
}

Describe 'Invoke-StepMain' {
    It 'writes exactly one JSON object and turns exceptions into sanitized failures' {
        $handler = { param($In, $Context) throw [System.Exception]::new("Geheim $($In['initialPassword'])") }
        $json = (New-TestStepInput) | ConvertTo-Json -Depth 10
        $original = [Console]::Out
        $writer = [System.IO.StringWriter]::new()
        try {
            [Console]::SetOut($writer)
            Invoke-StepMain -Handler $handler -Json $json
        }
        finally { [Console]::SetOut($original) }

        $lines = @($writer.ToString() -split "`r?`n" | Where-Object { $_ })
        $lines.Count | Should -Be 1
        $result = $lines[0] | ConvertFrom-Json
        $result.status | Should -Be 'failed'
        $result.reason | Should -BeExactly 'Unerwarteter Fehler (Exception).'
        $lines[0] | Should -Not -Match 'Geheim'
    }

    It 'rejects empty or invalid input with a safe reason' {
        { Read-StepInput -Json 'kein json' } | Should -Throw -ExpectedMessage 'Eingabe ist kein gültiges JSON.'
    }
}

Describe 'Invoke-StepChange' {
    It 'only records in dry-run' {
        $context = New-StepContext -DryRun $true
        $script:called = $false
        Invoke-StepChange -Context $context -Description 'X tun' -Action { $script:called = $true }
        $script:called | Should -BeFalse
        $context.PlannedActions | Should -Be @('X tun')
    }

    It 'executes and records outside dry-run' {
        $context = New-StepContext -DryRun $false
        (Invoke-StepChange -Context $context -Description 'Y' -Action { 42 }) | Should -Be 42
    }
}

Describe 'ConvertFrom-JeaResult' {
    It 'maps status, reason and planned actions' {
        $context = New-StepContext -DryRun $true
        $jea = [pscustomobject]@{ status = 'done'; reason = 'Dry-Run.'; plannedActions = @('Ordner anlegen'); output = [pscustomobject]@{ path = 'X' } }
        $result = ConvertFrom-JeaResult -Context $context -JeaResult $jea
        $result.status | Should -Be 'done'
        $result.plannedActions | Should -Be @('Ordner anlegen')
        $result.output.path | Should -Be 'X'
    }

    It 'fails on missing or unknown status' {
        $context = New-StepContext -DryRun $false
        (ConvertFrom-JeaResult -Context $context -JeaResult $null).status | Should -Be 'failed'
        (ConvertFrom-JeaResult -Context $context -JeaResult ([pscustomobject]@{ status = 'kaputt' })).status | Should -Be 'failed'
    }
}
