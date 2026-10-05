#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5.0' }
<#
    Tests of the JEA endpoint functions (validation, idempotency, dry-run) with mocked file,
    ACL and SMB access. Intended for Windows (paths with backslashes).
#>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Stubs.ps1')
    Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'jea', 'CCOnboarding', 'CCOnboarding.psd1')) -Force
    Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'jea', 'CCOnboardingLogon', 'CCOnboardingLogon.psd1')) -Force
    Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'jea', 'CCOnboardingSync', 'CCOnboardingSync.psd1')) -Force

    # File server DC01 (CC.Onboarding).
    $script:HomeConfig = @{
        HomeRoot         = 'F:\Home'
        ShareNamePattern = '{sam}$'
        ShareFullAccess  = @('BUILTIN\Administrators')
        NetbiosDomain    = 'CC'
        HomeOwner        = 'BUILTIN\Administrators'
        HomeUserRights   = @('Modify', 'FullControl')
        HomeAceAllowlist = @{
            'SYSTEM'                 = @('FullControl')
            'BUILTIN\Administrators' = @('FullControl')
            'CC\CC-Management'       = @('Modify')
        }
    }

    # Domain controller DC03 (CC.Onboarding.Logon).
    $script:LogonConfig = @{
        LogonScriptDirectory = 'C:\Windows\SYSVOL\sysvol\example.test\SCRIPTS'
        LogonFileNamePattern = '{sam}.bat'
        MaxLogonScriptBytes  = 65536
    }

    $script:Sids = @{
        'CC\lirion'              = 'S-1-5-21-1-2-3-1105'
        'BUILTIN\Administrators' = 'S-1-5-32-544'
        'SYSTEM'                 = 'S-1-5-18'
        'CC\CC-Management'       = 'S-1-5-21-1-2-3-2001'
    }

    function New-Rule {
        param([string] $Sid, [string] $Right, [string] $Inherit = 'ContainerInherit, ObjectInherit', [string] $Type = 'Allow')
        [pscustomobject]@{ Sid = $Sid; Right = $Right; Inherit = $Inherit; Type = $Type }
    }

    function New-AclState {
        param([object[]] $Rules, [bool] $Protected = $true, [string] $Owner = 'S-1-5-32-544')
        [pscustomobject]@{ Protected = $Protected; OwnerSid = $Owner; Rules = @($Rules) }
    }

    # The desired ACL for lirion with the default config (Modify + SYSTEM/Administrators FullControl).
    function New-DesiredRules {
        param([string] $UserRight = 'Modify')
        @(
            New-Rule 'S-1-5-21-1-2-3-1105' $UserRight
            New-Rule 'S-1-5-18' 'FullControl'
            New-Rule 'S-1-5-32-544' 'FullControl'
        )
    }

    $script:DefaultAces = @('SYSTEM=FullControl', 'BUILTIN\Administrators=FullControl')

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
        Mock -ModuleName CCOnboardingLogon Get-OnbEndpointConfig { $script:LogonConfig }
        Mock -ModuleName CCOnboardingLogon Test-OnbFile { $false }
        Mock -ModuleName CCOnboardingLogon Write-OnbFileBytes { }
    }

    It 'rejects invalid sam <Sam>' -TestCases @(
        @{ Sam = '..\..\Windows' }, @{ Sam = 'LIRION' }, @{ Sam = 'lirion.bat' }, @{ Sam = 'a b' }, @{ Sam = '' }, @{ Sam = 'abcdefghijklmnopqrstu' }
    ) {
        Assert-Rejected -Code 'invalid-sam' { Set-OnbLogonScript -Sam $Sam -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash }
        Should -Invoke -ModuleName CCOnboardingLogon Write-OnbFileBytes -Times 0 -Exactly
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
        Should -Invoke -ModuleName CCOnboardingLogon Write-OnbFileBytes -Times 0 -Exactly
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

    It 'endpoints are split: DC01 has only home functions, DC03 only the logon script' {
        @((Get-Command -Module CCOnboarding).Name | Sort-Object) | Should -Be @('New-OnbHomeFolder', 'New-OnbHomeShare')
        @((Get-Command -Module CCOnboardingLogon).Name) | Should -Be @('Set-OnbLogonScript')
    }
}

Describe 'Set-OnbLogonScript – Idempotenz' {
    BeforeEach {
        Mock -ModuleName CCOnboardingLogon Get-OnbEndpointConfig { $script:LogonConfig }
        Mock -ModuleName CCOnboardingLogon Write-OnbFileBytes { }
    }

    It 'writes a new file' {
        Mock -ModuleName CCOnboardingLogon Test-OnbFile { $false }
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash
        $result.status | Should -Be 'done'
        Should -Invoke -ModuleName CCOnboardingLogon Write-OnbFileBytes -Times 1 -Exactly -ParameterFilter { $Path -like '*\scripts\lirion.bat' }
    }

    It 'same hash → nothing to do' {
        Mock -ModuleName CCOnboardingLogon Test-OnbFile { $true }
        Mock -ModuleName CCOnboardingLogon Read-OnbFileBytes { $script:Good }
        (Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash).reason | Should -Match '^Datei vorhanden'
        Should -Invoke -ModuleName CCOnboardingLogon Write-OnbFileBytes -Times 0 -Exactly
    }

    It 'different content without force → needsInput, file untouched' {
        Mock -ModuleName CCOnboardingLogon Test-OnbFile { $true }
        Mock -ModuleName CCOnboardingLogon Read-OnbFileBytes { [Text.Encoding]::ASCII.GetBytes("manuell`r`n") }
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash
        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'logon-script-modified'
        $result.reason | Should -Match 'manuell angelegt oder geändert'
        Should -Invoke -ModuleName CCOnboardingLogon Write-OnbFileBytes -Times 0 -Exactly
    }

    It 'different content with force → overwritten' {
        Mock -ModuleName CCOnboardingLogon Test-OnbFile { $true }
        Mock -ModuleName CCOnboardingLogon Read-OnbFileBytes { [Text.Encoding]::ASCII.GetBytes("manuell`r`n") }
        (Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash -Force).status | Should -Be 'done'
        Should -Invoke -ModuleName CCOnboardingLogon Write-OnbFileBytes -Times 1 -Exactly
    }

    It 'dry-run writes nothing' {
        Mock -ModuleName CCOnboardingLogon Test-OnbFile { $false }
        $result = Set-OnbLogonScript -Sam lirion -ContentBase64 $script:GoodBase64 -Sha256 $script:GoodHash -DryRun
        @($result.plannedActions).Count | Should -Be 1
        Should -Invoke -ModuleName CCOnboardingLogon Write-OnbFileBytes -Times 0 -Exactly
    }
}


Describe 'New-OnbHomeFolder' {
    BeforeEach {
        Mock -ModuleName CCOnboarding Get-OnbEndpointConfig { $script:HomeConfig }
        Mock -ModuleName CCOnboarding Get-OnbSid { $script:Sids[$Principal] }
        Mock -ModuleName CCOnboarding Test-OnbDirectory { $false }
        Mock -ModuleName CCOnboarding New-OnbDirectory { }
        Mock -ModuleName CCOnboarding Set-OnbHomeAcl { }
        $script:AclState = New-AclState -Rules (New-DesiredRules)
        Mock -ModuleName CCOnboarding Get-OnbAclState { $script:AclState }
    }

    It 'new folder: inheritance off, owner Administrators, user Modify plus allowlisted ACEs' {
        $result = New-OnbHomeFolder -Sam lirion -UserRight Modify -AdditionalAces $script:DefaultAces
        $result.status | Should -Be 'done'
        Should -Invoke -ModuleName CCOnboarding New-OnbDirectory -Times 1 -Exactly -ParameterFilter { $Path -eq 'F:\Home\lirion' }
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 1 -Exactly -ParameterFilter {
            $OwnerSid -eq 'S-1-5-32-544' -and @($Rules).Count -eq 3 -and
            @($Rules | Where-Object { $_.Sid -eq 'S-1-5-21-1-2-3-1105' -and $_.Right -eq 'Modify' }).Count -eq 1 -and
            @($Rules | Where-Object { $_.Sid -eq 'S-1-5-18' -and $_.Right -eq 'FullControl' }).Count -eq 1
        }
    }

    It 'UserRight FullControl gives the user FullControl' {
        New-OnbHomeFolder -Sam lirion -UserRight FullControl -AdditionalAces $script:DefaultAces | Out-Null
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 1 -Exactly -ParameterFilter {
            @($Rules | Where-Object { $_.Sid -eq 'S-1-5-21-1-2-3-1105' -and $_.Right -eq 'FullControl' }).Count -eq 1
        }
    }

    It 'department ACE from the allowlist is accepted' {
        New-OnbHomeFolder -Sam lirion -UserRight Modify -AdditionalAces @($script:DefaultAces + 'cc\cc-management=Modify') | Out-Null
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 1 -Exactly -ParameterFilter {
            @($Rules).Count -eq 4 -and @($Rules | Where-Object { $_.Sid -eq 'S-1-5-21-1-2-3-2001' -and $_.Right -eq 'Modify' }).Count -eq 1
        }
    }

    It 'rejects <Name> → <Code> without touching the file system' -TestCases @(
        @{ Name = 'principal not in allowlist'; UserRight = 'Modify'; Aces = @('CC\Domain Users=FullControl'); Code = 'ace-not-allowed' }
        @{ Name = 'right not allowed for principal'; UserRight = 'Modify'; Aces = @('CC\CC-Management=FullControl'); Code = 'ace-not-allowed' }
        @{ Name = 'malformed entry'; UserRight = 'Modify'; Aces = @('CC\CC-Management'); Code = 'invalid-ace' }
        @{ Name = 'unknown right'; UserRight = 'Modify'; Aces = @('SYSTEM=Read'); Code = 'invalid-ace' }
        @{ Name = 'duplicate principal'; UserRight = 'Modify'; Aces = @('SYSTEM=FullControl', 'system=FullControl'); Code = 'invalid-ace' }
    ) {
        Assert-Rejected -Code $Code { New-OnbHomeFolder -Sam lirion -UserRight $UserRight -AdditionalAces $Aces }
        Should -Invoke -ModuleName CCOnboarding New-OnbDirectory -Times 0 -Exactly
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 0 -Exactly
    }

    It 'rejects a user right that the endpoint does not allow' {
        $script:HomeConfig.HomeUserRights = @('Modify')
        try {
            Assert-Rejected -Code 'ace-not-allowed' { New-OnbHomeFolder -Sam lirion -UserRight FullControl -AdditionalAces $script:DefaultAces }
        }
        finally {
            $script:HomeConfig.HomeUserRights = @('Modify', 'FullControl')
        }
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 0 -Exactly
    }

    It 'rejects an unknown user right already at parameter binding' {
        Assert-Rejected -Code 'ace-not-allowed' { New-OnbHomeFolder -Sam lirion -UserRight Read -AdditionalAces $script:DefaultAces }
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 0 -Exactly
    }

    It 'rejects path traversal via sam' {
        Assert-Rejected -Code 'invalid-sam' { New-OnbHomeFolder -Sam '..\Windows' -UserRight Modify }
        Should -Invoke -ModuleName CCOnboarding New-OnbDirectory -Times 0 -Exactly
    }

    It 'account not yet replicated → waiting' {
        Mock -ModuleName CCOnboarding Get-OnbSid -ParameterFilter { $Principal -eq 'CC\lirion' } { $null }
        $result = New-OnbHomeFolder -Sam lirion -UserRight Modify -AdditionalAces $script:DefaultAces
        $result.status | Should -Be 'waiting'
        $result.code | Should -Be 'account-not-resolvable'
        Should -Invoke -ModuleName CCOnboarding New-OnbDirectory -Times 0 -Exactly
    }

    It 'dry-run on a missing folder plans folder and ACL, creates nothing' {
        $result = New-OnbHomeFolder -Sam lirion -UserRight Modify -AdditionalAces $script:DefaultAces -DryRun
        @($result.plannedActions).Count | Should -Be 2
        Should -Invoke -ModuleName CCOnboarding New-OnbDirectory -Times 0 -Exactly
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 0 -Exactly
    }

    It 'existing folder with the desired ACL → done, nothing changed' {
        Mock -ModuleName CCOnboarding Test-OnbDirectory { $true }
        (New-OnbHomeFolder -Sam lirion -UserRight Modify -AdditionalAces $script:DefaultAces).reason | Should -Be 'Ordner und Rechte entsprechen dem Soll.'
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 0 -Exactly
    }

    It 'existing folder, <Name> → needsInput home-acl-mismatch' -TestCases @(
        @{ Name = 'user has FullControl instead of Modify'; State = { New-AclState -Rules (New-DesiredRules -UserRight FullControl) }; Expected = 'fehlt' }
        @{ Name = 'additional foreign ACE'; State = { New-AclState -Rules (@(New-DesiredRules) + (New-Rule 'S-1-1-0' 'FullControl')) }; Expected = 'zusätzlich' }
        @{ Name = 'inheritance active'; State = { New-AclState -Rules (New-DesiredRules) -Protected $false }; Expected = 'Vererbung' }
        @{ Name = 'other owner'; State = { New-AclState -Rules (New-DesiredRules) -Owner 'S-1-5-21-1-2-3-1105' }; Expected = 'Besitzer' }
        @{ Name = 'deny entry'; State = { New-AclState -Rules (@(New-DesiredRules) + (New-Rule 'S-1-1-0' 'Modify' -Type 'Deny')) }; Expected = 'Deny' }
    ) {
        Mock -ModuleName CCOnboarding Test-OnbDirectory { $true }
        $script:AclState = & $State
        $result = New-OnbHomeFolder -Sam lirion -UserRight Modify -AdditionalAces $script:DefaultAces
        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'home-acl-mismatch'
        $result.reason | Should -Match $Expected
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 0 -Exactly
    }

    It 'existing folder with deviating ACL and -Force → ACL replaced by the desired one' {
        Mock -ModuleName CCOnboarding Test-OnbDirectory { $true }
        $script:AclState = New-AclState -Rules (New-DesiredRules -UserRight FullControl) -Protected $false
        $result = New-OnbHomeFolder -Sam lirion -UserRight Modify -AdditionalAces $script:DefaultAces -Force
        $result.status | Should -Be 'done'
        Should -Invoke -ModuleName CCOnboarding Set-OnbHomeAcl -Times 1 -Exactly -ParameterFilter {
            @($Rules | Where-Object { $_.Sid -eq 'S-1-5-21-1-2-3-1105' -and $_.Right -eq 'Modify' }).Count -eq 1
        }
    }

    It 'ConvertTo-OnbRightName: <Rights> → <Expected>' -TestCases @(
        @{ Rights = 'Modify, Synchronize'; Expected = 'Modify' }
        @{ Rights = 'FullControl'; Expected = 'FullControl' }
        @{ Rights = 'ReadAndExecute, Synchronize'; Expected = 'ReadAndExecute, Synchronize' }
    ) {
        InModuleScope CCOnboarding -Parameters @{ Rights = $Rights; Expected = $Expected } {
            param($Rights, $Expected)
            ConvertTo-OnbRightName ([System.Security.AccessControl.FileSystemRights] $Rights) | Should -Be $Expected
        }
    }
}

Describe 'New-OnbHomeShare' {
    BeforeEach {
        Mock -ModuleName CCOnboarding Get-OnbEndpointConfig { $script:HomeConfig }
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
