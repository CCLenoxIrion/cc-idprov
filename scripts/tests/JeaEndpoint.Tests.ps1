#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5.0' }
<#
    Tests of the JEA endpoint functions (validation, idempotency, dry-run) with mocked file,
    ACL and SMB access. Intended for Windows (paths with backslashes).
#>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Stubs.ps1')
    Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'jea', 'CCOnboarding', 'CCOnboarding.psd1')) -Force
    Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'jea', 'CCOnboardingSync', 'CCOnboardingSync.psd1')) -Force

    $script:Config = @{
        HomeRoot             = 'F:\Home'
        ShareNamePattern     = '{sam}$'
        ShareFullAccess      = @('BUILTIN\Administrators')
        NetbiosDomain        = 'CC'
        LogonScriptDirectory = 'C:\Windows\SYSVOL\sysvol\example.test\scripts'
        LogonFileNamePattern = '{sam}.bat'
        MaxLogonScriptBytes  = 65536
    }

    function Get-Sha([byte[]] $Bytes) {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString('x2') }) -join '')
    }

    function Assert-Rejected {
        <#
            Any rejection counts: a throw (e.g. parameter binding refusing '' before the function
            body runs) or a result with status failed, the expected code and an "Abgelehnt" reason.
        #>
        param([Parameter(Mandatory)] [scriptblock] $Call, [Parameter(Mandatory)] [string] $Code)

        $result = $null
        $threw = $false
        try { $result = & $Call } catch { $threw = $true }
        if (-not $threw) {
            $result.status | Should -Be 'failed'
            $result.code | Should -Be $Code
            $result.reason | Should -Match '^Abgelehnt'
        }
    }

    $script:Good = [Text.Encoding]::ASCII.GetBytes("net use h: /del /y`r`nnet use h: \\dc01\lirion$`r`n")
    $script:GoodBase64 = [Convert]::ToBase64String($script:Good)
    $script:GoodHash = Get-Sha $script:Good
}

Describe 'Set-OnbLogonScript – Validierung' {
    BeforeEach {
        Mock -ModuleName CCOnboarding Get-OnbEndpointConfig { $script:Config }
        Mock -ModuleName CCOnboarding Test-OnbFile { $false }
        Mock -ModuleName CCOnboarding Write-OnbFileBytes { }
    }

    It 'rejects invalid sam <Sam>' -TestCases @(
        @{ Sam = '..\..\Windows' }, @{ Sam = 'LIRION' }, @{ Sam = 'lirion.bat' }, @{ Sam = 'a b' }, @{ Sam = '' }, @{ Sam = 'abcdefghijklmnopqrstu' }
    ) {
        Assert-Rejected -Code 'invalid-sam' { Set-OnbLogonScript -Sam $Sam -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash }
        Should -Invoke -ModuleName CCOnboarding Write-OnbFileBytes -Times 0 -Exactly
    }

    It 'rejects non-ASCII content' {
        $bytes = [Text.Encoding]::UTF8.GetBytes("rem Büro`r`n")
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 ([Convert]::ToBase64String($bytes)) -Sha256 (Get-Sha $bytes)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'non-ascii'
        $result.reason | Should -Be 'Abgelehnt: Inhalt enthält Nicht-ASCII-Zeichen.'
    }

    It 'rejects LF-only line endings' {
        $bytes = [Text.Encoding]::ASCII.GetBytes("net use h: /del /y`n")
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 ([Convert]::ToBase64String($bytes)) -Sha256 (Get-Sha $bytes)
        $result.code | Should -Be 'line-endings'
        $result.reason | Should -Be 'Abgelehnt: Zeilenenden müssen CRLF sein.'
    }

    It 'rejects a wrong hash' {
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 ('0' * 64)
        $result.code | Should -Be 'hash-mismatch'
        $result.reason | Should -Be 'Abgelehnt: SHA-256 stimmt nicht mit dem Inhalt überein.'
        Should -Invoke -ModuleName CCOnboarding Write-OnbFileBytes -Times 0 -Exactly
    }

    It 'rejects malformed hash and base64' {
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 'xyz'
        $result.code | Should -Be 'invalid-hash-format'
        $result.reason | Should -Match 'SHA-256 hat ein ungültiges Format'
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 '###' -Sha256 $script:GoodHash
        $result.code | Should -Be 'invalid-base64'
        $result.reason | Should -Be 'Abgelehnt: Inhalt ist kein gültiges Base64.'
    }

    It 'has no path parameter' {
        (Get-Command Set-OnbLogonScript).Parameters.Keys | Should -Not -Contain 'Path'
        (Get-Command New-OnbHomeFolder).Parameters.Keys | Should -Not -Contain 'Path'
        (Get-Command New-OnbHomeShare).Parameters.Keys | Should -Not -Contain 'Path'
    }
}

Describe 'Set-OnbLogonScript – Idempotenz' {
    BeforeEach {
        Mock -ModuleName CCOnboarding Get-OnbEndpointConfig { $script:Config }
        Mock -ModuleName CCOnboarding Write-OnbFileBytes { }
    }

    It 'writes a new file' {
        Mock -ModuleName CCOnboarding Test-OnbFile { $false }
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash
        $result.status | Should -Be 'done'
        Should -Invoke -ModuleName CCOnboarding Write-OnbFileBytes -Times 1 -Exactly -ParameterFilter { $Path -like '*\scripts\lirion.bat' }
    }

    It 'same hash → nothing to do' {
        Mock -ModuleName CCOnboarding Test-OnbFile { $true }
        Mock -ModuleName CCOnboarding Read-OnbFileBytes { $script:Good }
        (Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash).reason | Should -Match '^Datei vorhanden'
        Should -Invoke -ModuleName CCOnboarding Write-OnbFileBytes -Times 0 -Exactly
    }

    It 'different content without force → needsInput, file untouched' {
        Mock -ModuleName CCOnboarding Test-OnbFile { $true }
        Mock -ModuleName CCOnboarding Read-OnbFileBytes { [Text.Encoding]::ASCII.GetBytes("manuell`r`n") }
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash
        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'logon-script-modified'
        $result.reason | Should -Match 'manuell angelegt oder geändert'
        Should -Invoke -ModuleName CCOnboarding Write-OnbFileBytes -Times 0 -Exactly
    }

    It 'different content with force → overwritten' {
        Mock -ModuleName CCOnboarding Test-OnbFile { $true }
        Mock -ModuleName CCOnboarding Read-OnbFileBytes { [Text.Encoding]::ASCII.GetBytes("manuell`r`n") }
        (Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash -Force).status | Should -Be 'done'
        Should -Invoke -ModuleName CCOnboarding Write-OnbFileBytes -Times 1 -Exactly
    }

    It 'dry-run writes nothing' {
        Mock -ModuleName CCOnboarding Test-OnbFile { $false }
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash -DryRun
        @($result.plannedActions).Count | Should -Be 1
        Should -Invoke -ModuleName CCOnboarding Write-OnbFileBytes -Times 0 -Exactly
    }
}

Describe 'New-OnbHomeFolder' {
    BeforeEach {
        Mock -ModuleName CCOnboarding Get-OnbEndpointConfig { $script:Config }
        Mock -ModuleName CCOnboarding Test-OnbAccountResolvable { $true }
        Mock -ModuleName CCOnboarding New-OnbDirectory { }
        Mock -ModuleName CCOnboarding Set-OnbAcl { }
    }

    It 'rejects path traversal via sam' {
        Assert-Rejected -Code 'invalid-sam' { New-OnbHomeFolder -Sam '..\Windows' }
        Should -Invoke -ModuleName CCOnboarding New-OnbDirectory -Times 0 -Exactly
    }

    It 'account not yet replicated → waiting' {
        Mock -ModuleName CCOnboarding Test-OnbAccountResolvable { $false }
        $result = New-OnbHomeFolder -Sam lirion
        $result.status | Should -Be 'waiting'
        $result.code | Should -Be 'account-not-resolvable'
        Should -Invoke -ModuleName CCOnboarding New-OnbDirectory -Times 0 -Exactly
    }

    It 'dry-run on a missing folder plans folder and ACL, creates nothing' {
        Mock -ModuleName CCOnboarding Test-OnbDirectory { $false }
        $result = New-OnbHomeFolder -Sam lirion -DryRun
        @($result.plannedActions).Count | Should -Be 2
        Should -Invoke -ModuleName CCOnboarding New-OnbDirectory -Times 0 -Exactly
        Should -Invoke -ModuleName CCOnboarding Set-OnbAcl -Times 0 -Exactly
    }

    It 'existing folder with the user rule → nothing to do' {
        Mock -ModuleName CCOnboarding Test-OnbDirectory { $true }
        Mock -ModuleName CCOnboarding Get-OnbAcl {
            [pscustomobject]@{ Access = @([pscustomobject]@{
                        IdentityReference = [pscustomobject]@{ Value = 'CC\lirion' }
                        AccessControlType = 'Allow'
                        FileSystemRights  = [System.Security.AccessControl.FileSystemRights]::Modify
                        InheritanceFlags  = 3
                    }) }
        }
        (New-OnbHomeFolder -Sam lirion).reason | Should -Be 'Ordner und Rechte bereits vorhanden.'
        Should -Invoke -ModuleName CCOnboarding Set-OnbAcl -Times 0 -Exactly
    }
}

Describe 'New-OnbHomeShare' {
    BeforeEach {
        Mock -ModuleName CCOnboarding Get-OnbEndpointConfig { $script:Config }
        Mock -ModuleName CCOnboarding New-OnbShare { }
        Mock -ModuleName CCOnboarding Grant-OnbShareAccess { }
    }

    It 'creates the share with Change for the user' {
        Mock -ModuleName CCOnboarding Get-OnbShare { $null }
        Mock -ModuleName CCOnboarding Test-OnbDirectory { $true }
        (New-OnbHomeShare -Sam lirion).status | Should -Be 'done'
        Should -Invoke -ModuleName CCOnboarding New-OnbShare -Times 1 -Exactly -ParameterFilter { $Name -eq 'lirion$' -and $ChangeAccess -contains 'CC\lirion' }
    }

    It 'share with another path → needsInput' {
        Mock -ModuleName CCOnboarding Get-OnbShare { [pscustomobject]@{ Path = 'D:\Anders\lirion' } }
        $result = New-OnbHomeShare -Sam lirion
        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'share-path-mismatch'
        Should -Invoke -ModuleName CCOnboarding New-OnbShare -Times 0 -Exactly
    }

    It 'existing share with access → nothing to do' {
        Mock -ModuleName CCOnboarding Get-OnbShare { [pscustomobject]@{ Path = 'F:\Home\lirion' } }
        Mock -ModuleName CCOnboarding Get-OnbShareAccess { [pscustomobject]@{ AccountName = 'CC\lirion'; AccessControlType = 'Allow'; AccessRight = 'Change' } }
        (New-OnbHomeShare -Sam lirion).reason | Should -Be 'Freigabe und Rechte bereits vorhanden.'
        Should -Invoke -ModuleName CCOnboarding Grant-OnbShareAccess -Times 0 -Exactly
    }

    It 'invalid sam → rejected' {
        Assert-Rejected -Code 'invalid-sam' { New-OnbHomeShare -Sam 'lirion$x' }
        Should -Invoke -ModuleName CCOnboarding New-OnbShare -Times 0 -Exactly
    }
}

Describe 'Start-OnbDeltaSync' {
    It 'has no parameters' {
        $common = [System.Management.Automation.PSCmdlet]::CommonParameters
        @((Get-Command Start-OnbDeltaSync).Parameters.Keys | Where-Object { $_ -notin $common }).Count | Should -Be 0
    }

    It 'busy → waiting without raw message' {
        Mock -ModuleName CCOnboardingSync Invoke-OnbSyncCycle { throw 'Sync is busy, server CC01 user geheim' }
        $result = Start-OnbDeltaSync
        $result.status | Should -Be 'waiting'
        $result.code | Should -Be 'sync-busy'
        $result.reason | Should -Not -Match 'geheim'
    }

    It 'other error → failed sync-error without raw message' {
        Mock -ModuleName CCOnboardingSync Invoke-OnbSyncCycle { throw 'Connector CC01 password geheim' }
        $result = Start-OnbDeltaSync
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'sync-error'
        $result.reason | Should -Not -Match 'geheim'
    }

    It 'success → done' {
        Mock -ModuleName CCOnboardingSync Invoke-OnbSyncCycle { [pscustomobject]@{ Result = 'Success' } }
        (Start-OnbDeltaSync).status | Should -Be 'done'
    }
}
