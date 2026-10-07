#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5.0' }
<# Tests of the shared module Onboarding.Step.psm1. Run: Invoke-Pester ./scripts/tests #>

BeforeDiscovery {
    . (Join-Path $PSScriptRoot 'TestEnv.ps1')
}

BeforeAll {
    if ($PSVersionTable.PSVersion.Major -lt 7) { return }
    . (Join-Path $PSScriptRoot 'Stubs.ps1')
    Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
}

Describe 'ConvertTo-LdapFilterValue (RFC 4515)' -Skip:$SkipUnlessPwsh7 {
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

Describe 'Assert-SamAccountName' -Skip:$SkipUnlessPwsh7 {
    It 'accepts <Sam>' -TestCases @(@{ Sam = 'lirion' }, @{ Sam = 'a1' }, @{ Sam = 'abcdefghijklmnopqrst' }) {
        { Assert-SamAccountName $Sam } | Should -Not -Throw
    }

    It 'rejects <Sam>' -TestCases @(
        @{ Sam = '' }, @{ Sam = 'L.Irion' }, @{ Sam = 'lirion*' }, @{ Sam = '..\x' }, @{ Sam = 'abcdefghijklmnopqrstu' }, @{ Sam = 'müller' }
    ) {
        { Assert-SamAccountName $Sam } | Should -Throw
    }
}

Describe 'Get-SafeErrorReason' -Skip:$SkipUnlessPwsh7 {
    It 'never returns raw exception messages' {
        $raw = [System.Exception]::new('Kennwort Geheim-Start!2026 für CN=Lenox Irion abgelehnt')
        $reason = Get-SafeErrorReason $raw
        $reason | Should -Not -Match 'Geheim'
        $reason | Should -Not -Match 'Lenox'
        $reason | Should -BeExactly 'Unerwarteter Fehler (Exception).'
    }

    It 'maps AD exceptions to fixed codes and texts' {
        $safe = Get-SafeError ([Microsoft.ActiveDirectory.Management.ADPasswordComplexityException]::new('Geheim-Start!2026'))
        $safe.code | Should -Be 'password-policy'
        $safe.reason | Should -BeExactly 'Startpasswort entspricht nicht der Domänen-Kennwortrichtlinie.'
    }

    It 'returns code and message of safe exceptions' {
        $safe = Get-SafeError (New-SafeException -Code 'group-not-found' 'Gruppe X nicht gefunden.')
        $safe.code | Should -Be 'group-not-found'
        $safe.reason | Should -BeExactly 'Gruppe X nicht gefunden.'
        (Get-SafeError (New-SafeException 'Ohne Code.')).code | Should -Be 'invalid-input'
    }

    It 'carries the status of transient safe errors (waiting)' {
        $safe = Get-SafeError (New-SafeException -Status waiting -Code 'graph-throttled' 'Graph vorübergehend nicht verfügbar.')
        $safe.status | Should -Be 'waiting'
        $safe.code | Should -Be 'graph-throttled'
        (Get-SafeError (New-SafeException 'x')).status | Should -Be 'failed'
        (Get-SafeError ([System.Exception]::new('roh'))).status | Should -Be 'failed'
    }

    It 'Invoke-StepMain keeps the waiting status of a safe error' {
        $handler = { param($In, $Context) throw (New-SafeException -Status waiting -Code 'graph-throttled' 'später erneut') }
        $json = (New-TestStepInput) | ConvertTo-Json -Depth 10
        $original = [Console]::Out
        $writer = [System.IO.StringWriter]::new()
        try {
            [Console]::SetOut($writer)
            Invoke-StepMain -Handler $handler -Json $json
        }
        finally { [Console]::SetOut($original) }

        $result = $writer.ToString().Trim() | ConvertFrom-Json
        $result.status | Should -Be 'waiting'
        $result.code | Should -Be 'graph-throttled'
    }

    It 'rejects malformed codes on safe exceptions' {
        { New-SafeException -Code 'Kein Code!' 'x' } | Should -Throw
    }
}

Describe 'New-StepResult codes' -Skip:$SkipUnlessPwsh7 {
    It 'done needs no code' {
        (New-StepResult -Context (New-StepContext -DryRun $false) -Status done).code | Should -BeNullOrEmpty
    }

    It '<Status> without code becomes unspecified (visible script bug)' -TestCases @(
        @{ Status = 'failed' }, @{ Status = 'needsInput' }, @{ Status = 'waiting' }
    ) {
        (New-StepResult -Context (New-StepContext -DryRun $false) -Status $Status).code | Should -Be 'unspecified'
    }

    It 'malformed code becomes invalid-code' {
        (New-StepResult -Context (New-StepContext -DryRun $false) -Status failed -Code 'Freitext mit Leerzeichen').code | Should -Be 'invalid-code'
    }

    It 'no step script path produces unspecified' {
        $files = Get-ChildItem -Path (Join-Path (Join-Path $PSScriptRoot '..') 'steps'), (Join-Path (Join-Path $PSScriptRoot '..') 'common') -Include '*.ps1', '*.psm1' -Recurse
        foreach ($file in $files) {
            foreach ($line in (Get-Content -LiteralPath $file.FullName)) {
                if ($line -match 'New-StepResult\b.*-Status (failed|needsInput|waiting|manualTask|skipped)\b') {
                    $line | Should -Match '-Code ' -Because "$($file.Name): $line"
                }
            }
        }
    }
}

Describe 'Invoke-StepMain' -Skip:$SkipUnlessPwsh7 {
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
        $result.code | Should -Be 'unexpected-error'
        $result.reason | Should -BeExactly 'Unerwarteter Fehler (Exception).'
        $lines[0] | Should -Not -Match 'Geheim'
    }

    It 'rejects empty or invalid input with a safe reason' {
        { Read-StepInput -Json 'kein json' } | Should -Throw -ExpectedMessage 'Eingabe ist kein gültiges JSON.'
        try { Read-StepInput -Json 'kein json' } catch { (Get-SafeError $_).code | Should -Be 'invalid-input' }
    }
}

Describe 'Invoke-StepChange' -Skip:$SkipUnlessPwsh7 {
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

Describe 'ConvertFrom-JeaResult' -Skip:$SkipUnlessPwsh7 {
    It 'maps status, reason and planned actions' {
        $context = New-StepContext -DryRun $true
        $jea = [pscustomobject]@{ status = 'done'; reason = 'Dry-Run.'; plannedActions = @('Ordner anlegen'); output = [pscustomobject]@{ path = 'X' } }
        $result = ConvertFrom-JeaResult -Context $context -JeaResult $jea
        $result.status | Should -Be 'done'
        $result.code | Should -BeNullOrEmpty
        $result.plannedActions | Should -Be @('Ordner anlegen')
        $result.output.path | Should -Be 'X'
    }

    It 'fails on missing or unknown status' {
        $context = New-StepContext -DryRun $false
        foreach ($jea in @($null, [pscustomobject]@{ status = 'kaputt' })) {
            $result = ConvertFrom-JeaResult -Context $context -JeaResult $jea
            $result.status | Should -Be 'failed'
            $result.code | Should -Be 'jea-invalid-result'
        }
    }

    It 'passes the endpoint code through' {
        $jea = [pscustomobject]@{ status = 'failed'; code = 'hash-mismatch'; reason = 'Abgelehnt: SHA-256 stimmt nicht mit dem Inhalt überein.' }
        $result = ConvertFrom-JeaResult -Context (New-StepContext -DryRun $false) -JeaResult $jea
        $result.code | Should -Be 'hash-mismatch'
    }
}
