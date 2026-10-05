#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5.0' }
<# Tests of AD.CreateUser, AD.Groups, AD.Enable with mocked AD cmdlets. #>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Stubs.ps1')
    . ([System.IO.Path]::Combine($PSScriptRoot, '..', 'steps', 'AD.CreateUser.ps1'))
    . ([System.IO.Path]::Combine($PSScriptRoot, '..', 'steps', 'AD.Groups.ps1'))
    . ([System.IO.Path]::Combine($PSScriptRoot, '..', 'steps', 'AD.Enable.ps1'))

    $script:Password = 'Geheim-Start!2026'
    $script:Manager = [pscustomobject]@{ DistinguishedName = 'CN=Petra Vogel,OU=Users,DC=example,DC=test'; ObjectGUID = [guid] '0f3c1a52-1b8e-4f5a-9c2d-000000000001' }

    function New-ExistingUser {
        param([hashtable] $Overrides = @{}, [string] $RequestId = '6f1c2e3d-4b5a-4c6d-8e9f-0a1b2c3d4e5f')
        $user = [ordered]@{
            ObjectGUID           = [guid] 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
            SID                  = 'S-1-5-21-1111111111-2222222222-3333333333-1105'
            DistinguishedName    = 'CN=Lenox Irion,OU=Users,OU=Technical,DC=example,DC=test'
            givenName            = 'Lenox'
            sn                   = 'Irion'
            displayName          = 'Lenox Irion'
            mail                 = 'l.irion@example.test'
            userPrincipalName    = 'l.irion@example.test'
            department           = 'Vertrieb'
            company              = 'Example GmbH'
            scriptPath           = 'lirion.bat'
            telephoneNumber      = '+49 1234 5678-12'
            extensionAttribute1  = 'Dr.'
            extensionAttribute15 = $RequestId
            manager              = $script:Manager.DistinguishedName
            proxyAddresses       = @('SMTP:l.irion@example.test', 'smtp:l.irion@example.com')
            Enabled              = $false
            memberOf             = @()
        }
        foreach ($key in $Overrides.Keys) { $user[$key] = $Overrides[$key] }
        return [pscustomobject] $user
    }
}

Describe 'AD.CreateUser' {
    BeforeEach {
        Mock Get-ADUser -ParameterFilter { $Identity } { $script:Manager }
        Mock Get-ADObject { $null }
        Mock New-ADUser { [pscustomobject]@{ ObjectGUID = [guid] '11111111-2222-3333-4444-555555555555'; SID = 'S-1-5-21-1111111111-2222222222-3333333333-1234' } }
        Mock Set-ADUser { }
        Mock Set-ADAccountPassword { }
    }

    It 'creates a disabled account with password change at logon' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { $null }
        $result = Invoke-AdCreateUser -In (New-TestStepInput) -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'done'
        $result.directoryObjectGuid | Should -Be '11111111-2222-3333-4444-555555555555'
        $result.directoryObjectSid | Should -Be 'S-1-5-21-1111111111-2222222222-3333333333-1234'
        Should -Invoke New-ADUser -Times 1 -Exactly -ParameterFilter {
            $Enabled -eq $false -and $ChangePasswordAtLogon -eq $true -and $Path -eq 'OU=Users,OU=Technical,DC=example,DC=test' -and
            $OtherAttributes['extensionAttribute15'] -eq '6f1c2e3d-4b5a-4c6d-8e9f-0a1b2c3d4e5f' -and
            $OtherAttributes['extensionAttribute1'] -eq 'Dr.' -and
            @($OtherAttributes['proxyAddresses']).Count -eq 2 -and
            $AccountPassword -is [securestring]
        }
        (ConvertTo-StepJson $result) | Should -Not -Match 'Geheim-Start'
    }

    It 'uses an escaped LDAP filter, never -Filter' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { $null }
        Invoke-AdCreateUser -In (New-TestStepInput) -Context (New-StepContext -DryRun $false) | Out-Null
        Should -Invoke Get-ADUser -ParameterFilter { $LDAPFilter -like '*(sAMAccountName=lirion)*' }
    }

    It 'is idempotent: own account with all attributes → no write, no password reset' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser }
        $result = Invoke-AdCreateUser -In (New-TestStepInput) -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'done'
        $result.directoryObjectGuid | Should -Be 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
        $result.directoryObjectSid | Should -Be 'S-1-5-21-1111111111-2222222222-3333333333-1105'
        Should -Invoke New-ADUser -Times 0 -Exactly
        Should -Invoke Set-ADUser -Times 0 -Exactly
        Should -Invoke Set-ADAccountPassword -Times 0 -Exactly
    }

    It 'corrects drift of an own account but never resets the password' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser -Overrides @{ department = 'Falsch'; proxyAddresses = @('SMTP:l.irion@example.test') } }
        $result = Invoke-AdCreateUser -In (New-TestStepInput) -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'done'
        $result.reason | Should -Match 'department'
        Should -Invoke Set-ADUser -Times 1 -Exactly -ParameterFilter { $Replace['department'] -eq 'Vertrieb' -and @($Replace['proxyAddresses']).Count -eq 2 }
        Should -Invoke Set-ADAccountPassword -Times 0 -Exactly
        Should -Invoke New-ADUser -Times 0 -Exactly
    }

    It 'recognizes the own account by stored objectGUID when the request-id attribute is missing' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser -RequestId '' -Overrides @{ extensionAttribute15 = $null } }
        $in = New-TestStepInput -DirectoryObjectGuid 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
        $result = Invoke-AdCreateUser -In $in -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'done'
        $result.code | Should -BeNullOrEmpty
        # The missing request-id attribute is drift and gets written; the password is not touched.
        Should -Invoke Set-ADUser -Times 1 -Exactly -ParameterFilter { $Replace['extensionAttribute15'] -eq '6f1c2e3d-4b5a-4c6d-8e9f-0a1b2c3d4e5f' }
        Should -Invoke Set-ADAccountPassword -Times 0 -Exactly
    }

    It 'regression: own account in a deep OU (four OU RDNs) → done' {
        # Get-ParentDn used [regex]::Split($dn, $pattern, 2): the 2 is RegexOptions, not a count,
        # so the DN was split at every comma and every own account looked like "another OU".
        $ou = 'OU=Users,OU=Biology,OU=Medical,OU=CleanControlling,DC=example,DC=test'
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser -Overrides @{ DistinguishedName = "CN=Lenox Irion,$ou" } }
        $in = New-TestStepInput
        $in['identity']['ouDn'] = $ou
        $result = Invoke-AdCreateUser -In $in -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'done'
        $result.reason | Should -Be 'Konto existiert bereits mit allen Attributen.'
        $result.directoryObjectGuid | Should -Be 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
        Should -Invoke Set-ADUser -Times 0 -Exactly
        Should -Invoke New-ADUser -Times 0 -Exactly
    }

    It 'own account: configured OU with spaces and other case still matches' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser }
        $in = New-TestStepInput
        $in['identity']['ouDn'] = 'ou=Users, OU=Technical, dc=example, DC=test'
        (Invoke-AdCreateUser -In $in -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
    }

    It 'foreign account with the same sam → needsInput foreign-account' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser -RequestId 'andere-id' }
        $result = Invoke-AdCreateUser -In (New-TestStepInput) -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'foreign-account'
        $result.reason | Should -Match "sAMAccountName 'lirion' existiert bereits"
        Should -Invoke Set-ADUser -Times 0 -Exactly
        Should -Invoke New-ADUser -Times 0 -Exactly
    }

    It 'own account in another OU → needsInput ou-mismatch' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser -Overrides @{ DistinguishedName = 'CN=Lenox Irion,OU=Andere,OU=Technical,DC=example,DC=test' } }
        $result = Invoke-AdCreateUser -In (New-TestStepInput) -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'ou-mismatch'
        $result.reason | Should -Be 'Das Konto des Auftrags liegt in einer anderen OU als konfiguriert.'
        $result.directoryObjectGuid | Should -Be 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
        Should -Invoke Set-ADUser -Times 0 -Exactly
    }

    It 'UPN or mail used by another object → needsInput address-in-use' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { $null }
        Mock Get-ADObject -ParameterFilter { $LDAPFilter -like '(|(userPrincipalName=*' } { [pscustomobject]@{ ObjectGUID = [guid]::NewGuid() } }
        $result = Invoke-AdCreateUser -In (New-TestStepInput) -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'address-in-use'
        Should -Invoke New-ADUser -Times 0 -Exactly
    }

    It 'CN already used in the target OU → needsInput cn-in-use' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { $null }
        Mock Get-ADObject -ParameterFilter { $LDAPFilter -eq '(cn=Lenox Irion)' } { [pscustomobject]@{ ObjectGUID = [guid]::NewGuid() } }
        $result = Invoke-AdCreateUser -In (New-TestStepInput) -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'cn-in-use'
        Should -Invoke New-ADUser -Times 0 -Exactly
    }

    It 'dry-run plans the creation and changes nothing' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { $null }
        $result = Invoke-AdCreateUser -In (New-TestStepInput -DryRun) -Context (New-StepContext -DryRun $true)

        $result.status | Should -Be 'done'
        $result.dryRun | Should -BeTrue
        @($result.plannedActions).Count | Should -Be 1
        $result.plannedActions[0] | Should -Match "Konto 'lirion' deaktiviert anlegen"
        Should -Invoke New-ADUser -Times 0 -Exactly
    }

    It 'error with secrets in the message → sanitized reason' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { $null }
        Mock New-ADUser { throw [Microsoft.ActiveDirectory.Management.ADPasswordComplexityException]::new("Kennwort $script:Password zu schwach") }
        $context = New-StepContext -DryRun $false
        $result = Invoke-StepHandler ${function:Invoke-AdCreateUser} (New-TestStepInput) $context

        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'password-policy'
        $result.reason | Should -BeExactly 'Startpasswort entspricht nicht der Domänen-Kennwortrichtlinie.'
        (ConvertTo-StepJson $result) | Should -Not -Match 'Geheim-Start'
    }

    It 'missing password → failed without calling New-ADUser' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { $null }
        $in = New-TestStepInput
        $in.Remove('initialPassword')
        $result = Invoke-AdCreateUser -In $in -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'missing-password'
        Should -Invoke New-ADUser -Times 0 -Exactly
    }
}

Describe 'AD.Groups' {
    BeforeEach {
        Mock Add-ADGroupMember { }
        Mock Get-ADGroup { [pscustomobject]@{ DistinguishedName = 'CN=GG-Vertrieb,OU=Groups,DC=example,DC=test' } }
    }

    It 'adds missing memberships' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser }
        $result = Invoke-AdGroups -In (New-TestStepInput -Step 'AD.Groups') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'done'
        Should -Invoke Add-ADGroupMember -Times 1 -Exactly
    }

    It 'is idempotent' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser -Overrides @{ memberOf = @('CN=GG-Vertrieb,OU=Groups,DC=example,DC=test') } }
        (Invoke-AdGroups -In (New-TestStepInput -Step 'AD.Groups') -Context (New-StepContext -DryRun $false)).reason | Should -Be 'Alle Gruppen bereits zugewiesen.'
        Should -Invoke Add-ADGroupMember -Times 0 -Exactly
    }

    It 'unknown group → failed with the group name only' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser }
        Mock Get-ADGroup { throw [Microsoft.ActiveDirectory.Management.ADIdentityNotFoundException]::new('raw detail') }
        $context = New-StepContext -DryRun $false
        $result = Invoke-StepHandler ${function:Invoke-AdGroups} (New-TestStepInput -Step 'AD.Groups') $context
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'group-not-found'
        $result.reason | Should -BeExactly "Gruppe 'GG-Vertrieb' nicht gefunden."
    }

    It 'dry-run does not add' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser }
        $result = Invoke-AdGroups -In (New-TestStepInput -Step 'AD.Groups' -DryRun) -Context (New-StepContext -DryRun $true)
        @($result.plannedActions).Count | Should -Be 1
        Should -Invoke Add-ADGroupMember -Times 0 -Exactly
    }

    It 'foreign account → needsInput foreign-account' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser -RequestId 'fremd' }
        $result = Invoke-AdGroups -In (New-TestStepInput -Step 'AD.Groups') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'foreign-account'
        Should -Invoke Add-ADGroupMember -Times 0 -Exactly
    }
}

Describe 'AD.Enable' {
    BeforeEach { Mock Enable-ADAccount { } }

    It 'enables a disabled own account' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser }
        (Invoke-AdEnable -In (New-TestStepInput -Step 'AD.Enable') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Enable-ADAccount -Times 1 -Exactly
    }

    It 'is idempotent' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { New-ExistingUser -Overrides @{ Enabled = $true } }
        (Invoke-AdEnable -In (New-TestStepInput -Step 'AD.Enable') -Context (New-StepContext -DryRun $false)).reason | Should -Be 'Konto bereits aktiviert.'
        Should -Invoke Enable-ADAccount -Times 0 -Exactly
    }

    It 'missing account → failed account-not-found' {
        Mock Get-ADUser -ParameterFilter { $LDAPFilter } { $null }
        $result = Invoke-AdEnable -In (New-TestStepInput -Step 'AD.Enable') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'account-not-found'
        Should -Invoke Enable-ADAccount -Times 0 -Exactly
    }
}

Describe 'Get-AdUserSid' {
    It 'returns <Expected> for <Name>' -TestCases @(
        @{ Name = 'SID property'; User = [pscustomobject]@{ SID = 'S-1-5-21-1-2-3-1105' }; Expected = 'S-1-5-21-1-2-3-1105' }
        @{ Name = 'objectSid property'; User = [pscustomobject]@{ objectSid = 'S-1-5-21-1-2-3-500' }; Expected = 'S-1-5-21-1-2-3-500' }
        @{ Name = 'well-known SID'; User = [pscustomobject]@{ SID = 'S-1-5-18' }; Expected = '' }
        @{ Name = 'no SID'; User = [pscustomobject]@{ ObjectGUID = [guid]::NewGuid() }; Expected = '' }
        @{ Name = 'null'; User = $null; Expected = '' }
    ) {
        Get-AdUserSid $User | Should -BeExactly $Expected
    }
}

Describe 'DN helpers' {
    It 'Get-ParentDn of <Dn>' -TestCases @(
        @{ Dn = 'CN=Lenox Irion,OU=Users,OU=Biology,OU=Medical,OU=CleanControlling,DC=example,DC=test'; Parent = 'OU=Users,OU=Biology,OU=Medical,OU=CleanControlling,DC=example,DC=test' }
        @{ Dn = 'CN=Irion\, Lenox,OU=Users,DC=example,DC=test'; Parent = 'OU=Users,DC=example,DC=test' }
        @{ Dn = 'CN=Back\\,OU=Users,DC=example,DC=test'; Parent = 'OU=Users,DC=example,DC=test' }
        @{ Dn = 'DC=test'; Parent = '' }
        @{ Dn = ''; Parent = '' }
    ) {
        Get-ParentDn $Dn | Should -BeExactly $Parent
    }

    It 'Test-SameDn <Left> = <Right> → <Expected>' -TestCases @(
        @{ Left = 'OU=Users,OU=Technical,DC=example,DC=test'; Right = 'ou=users, OU=Technical , dc=example,DC=test'; Expected = $true }
        @{ Left = 'OU=Users,OU=Technical,DC=example,DC=test'; Right = 'OU=Users,DC=example,DC=test'; Expected = $false }
        @{ Left = 'OU=A\,B,DC=example,DC=test'; Right = 'OU=A\,B,DC=example,DC=test'; Expected = $true }
        @{ Left = 'OU=A\,B,DC=example,DC=test'; Right = 'OU=A,OU=B,DC=example,DC=test'; Expected = $false }
    ) {
        Test-SameDn $Left $Right | Should -Be $Expected
    }
}
