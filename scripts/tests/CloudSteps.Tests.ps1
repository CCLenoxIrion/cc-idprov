#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5.0' }
<# Tests of the cloud step scripts (phase 4b) with mocked Graph/EXO/Teams cmdlets. #>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Stubs.ps1')
    foreach ($step in 'Entra.WaitUser', 'Entra.WaitEnabled', 'Entra.UsageLocation', 'Entra.AssignLicense', 'Entra.WaitLicense',
        'EXO.WaitMailbox', 'EXO.DisableNewOutlook', 'EXO.SharedMailboxes',
        'Teams.WaitUser', 'Teams.Phone', 'Teams.VoiceRouting', 'Teams.Voicemail', 'Teams.Forwarding') {
        . ([System.IO.Path]::Combine($PSScriptRoot, '..', 'steps', "$step.ps1"))
    }

    $script:Sid = 'S-1-5-21-1111111111-2222222222-3333333333-1105'
    $script:Guid = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
    $script:SpbId = '00000000-0000-0000-0000-00000000a5b1'
    $script:EvId = '00000000-0000-0000-0000-00000000e5e1'

    function New-CloudStepInput {
        param([string] $Step, [switch] $DryRun, [switch] $NoSid)
        $in = New-TestStepInput -Step $Step -DryRun:$DryRun -DirectoryObjectGuid $script:Guid
        if (-not $NoSid) { $in['directoryObjectSid'] = $script:Sid }
        return $in
    }

    function New-GraphUser {
        param([hashtable] $Overrides = @{})
        $user = @{
            id                           = '99999999-0000-0000-0000-000000000001'
            userPrincipalName            = 'l.irion@example.test'
            accountEnabled               = $false
            usageLocation                = 'DE'
            onPremisesSyncEnabled        = $true
            onPremisesSecurityIdentifier = $script:Sid
            onPremisesImmutableId        = [Convert]::ToBase64String(([guid] $script:Guid).ToByteArray())
            assignedLicenses             = @()
            licenseAssignmentStates      = @()
        }
        foreach ($key in $Overrides.Keys) { $user[$key] = $Overrides[$key] }
        return $user
    }

    function New-Skus {
        param([int] $FreeSpb = 5, [int] $FreeEv = 5)
        return @{
            value = @(
                @{ skuId = $script:SpbId; skuPartNumber = 'SPB'; consumedUnits = 10; prepaidUnits = @{ enabled = 10 + $FreeSpb }
                    servicePlans = @(@{ servicePlanId = 'p-yammer'; servicePlanName = 'YAMMER_ENTERPRISE' }) }
                @{ skuId = $script:EvId; skuPartNumber = 'MCOEV'; consumedUnits = 3; prepaidUnits = @{ enabled = 3 + $FreeEv }; servicePlans = @() }
            )
        }
    }

    function Assert-NoConnectionDetails {
        param($Result)
        $json = ConvertTo-StepJson $Result
        foreach ($secret in $script:TestTenantId, $script:TestAppId, $script:TestThumbprint, $script:TestToken, 'GEHEIMER-TOKEN') {
            $json | Should -Not -Match ([regex]::Escape($secret))
        }
    }
}

Describe 'CloudHelpers' {
    It 'ConvertTo-ODataLiteral doubles quotes and URL-encodes <Value>' -TestCases @(
        @{ Value = "o'brien#x%+&@example.test"; Expected = "'o%27%27brien%23x%25%2B%26%40example.test'" }
        @{ Value = 'plain'; Expected = "'plain'" }
        @{ Value = ''; Expected = "''" }
    ) {
        ConvertTo-ODataLiteral $Value | Should -BeExactly $Expected
    }

    It 'escapes the UPN in the Graph path' {
        Get-GraphUserUri -UserPrincipalName "o'brien#x@example.test" -Suffix '?$select=id' |
            Should -BeExactly 'v1.0/users/o%27brien%23x%40example.test?$select=id'
    }

    It 'ConvertTo-PsLiteral doubles single quotes' {
        ConvertTo-PsLiteral "Team's Policy" | Should -BeExactly "'Team''s Policy'"
    }

    It 'ownership: <Name> → <Expected>' -TestCases @(
        @{ Name = 'SID matches'; Overrides = @{}; NoSid = $false; Expected = $true }
        @{ Name = 'SID differs'; Overrides = @{ onPremisesSecurityIdentifier = 'S-1-5-21-9-9-9-1105' }; NoSid = $false; Expected = $false }
        @{ Name = 'SID stored, immutable id would match but SID differs'; Overrides = @{ onPremisesSecurityIdentifier = $null }; NoSid = $false; Expected = $false }
        @{ Name = 'no SID stored, immutable id matches'; Overrides = @{ onPremisesSecurityIdentifier = $null }; NoSid = $true; Expected = $true }
        @{ Name = 'no SID stored, immutable id differs'; Overrides = @{ onPremisesImmutableId = 'AAAA' }; NoSid = $true; Expected = $false }
    ) {
        $in = New-CloudStepInput -Step 'Entra.WaitUser' -NoSid:$NoSid
        Test-OnbOwnCloudUser -User (New-GraphUser -Overrides $Overrides) -In $in | Should -Be $Expected
    }

    It 'Graph HTTP <Status> → <Code>' -TestCases @(
        @{ Status = 429; Code = 'graph-throttled'; ResultStatus = 'waiting' }
        @{ Status = 503; Code = 'graph-throttled'; ResultStatus = 'waiting' }
        @{ Status = 403; Code = 'graph-access-denied'; ResultStatus = 'failed' }
        @{ Status = 400; Code = 'graph-error'; ResultStatus = 'failed' }
    ) {
        Mock Connect-MgGraph { }
        Mock Disconnect-MgGraph { }
        $script:HttpStatus = $Status
        Mock Invoke-MgGraphRequest { throw [OnbTest.FakeGraphHttpException]::new($script:HttpStatus, "raw $script:TestToken") }
        $context = New-StepContext -DryRun $false
        $result = Invoke-StepHandler ${function:Invoke-EntraUsageLocation} (New-CloudStepInput -Step 'Entra.UsageLocation') $context

        $result.status | Should -Be $ResultStatus
        $result.code | Should -Be $Code
        Assert-NoConnectionDetails $result
        Should -Invoke Disconnect-MgGraph -Times 1 -Exactly
    }

    It 'Graph 404 → user not visible yet (waiting)' {
        Mock Connect-MgGraph { }
        Mock Disconnect-MgGraph { }
        Mock Invoke-MgGraphRequest { throw [OnbTest.FakeGraphHttpException]::new(404, 'not found') }
        $result = Invoke-EntraWaitUser -In (New-CloudStepInput -Step 'Entra.WaitUser') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'waiting'
        $result.code | Should -Be 'entra-user-pending'
    }

    It 'failed connect → cloud-auth-failed without the module message' {
        Mock Connect-MgGraph { throw [System.Exception]::new("AADSTS700027 token $script:TestToken for app $script:TestAppId") }
        Mock Invoke-MgGraphRequest { throw 'should not be called' }
        $result = Invoke-StepHandler ${function:Invoke-EntraWaitUser} (New-CloudStepInput -Step 'Entra.WaitUser') (New-StepContext -DryRun $false)

        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'cloud-auth-failed'
        Assert-NoConnectionDetails $result
        Should -Invoke Invoke-MgGraphRequest -Times 0 -Exactly
    }
}

Describe 'Entra steps' {
    BeforeEach {
        Mock Connect-MgGraph { $script:TestToken }
        Mock Disconnect-MgGraph { }
        $script:User = New-GraphUser
        $script:FreeEv = 5
        Mock Invoke-MgGraphRequest -ParameterFilter { $Method -eq 'GET' -and $Uri -like 'v1.0/users/*' } { $script:User }
        Mock Invoke-MgGraphRequest -ParameterFilter { $Uri -eq 'v1.0/subscribedSkus' } { New-Skus -FreeEv $script:FreeEv }
        Mock Invoke-MgGraphRequest -ParameterFilter { $Method -in 'PATCH', 'POST' } { $null }
    }

    It 'WaitUser: own user → done with object id' {
        $result = Invoke-EntraWaitUser -In (New-CloudStepInput -Step 'Entra.WaitUser') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'done'
        $result.output.entraObjectId | Should -Be '99999999-0000-0000-0000-000000000001'
        Should -Invoke Invoke-MgGraphRequest -ParameterFilter { $Uri -like 'v1.0/users/l.irion%40example.test?$select=*onPremisesSecurityIdentifier*' }
    }

    It 'WaitUser: foreign user → needsInput foreign-cloud-account' {
        $script:User = New-GraphUser -Overrides @{ onPremisesSecurityIdentifier = 'S-1-5-21-9-9-9-500' }
        $result = Invoke-EntraWaitUser -In (New-CloudStepInput -Step 'Entra.WaitUser') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'needsInput'
        $result.code | Should -Be 'foreign-cloud-account'
    }

    It 'WaitEnabled: false → waiting, true → done' {
        (Invoke-EntraWaitEnabled -In (New-CloudStepInput -Step 'Entra.WaitEnabled') -Context (New-StepContext -DryRun $false)).code | Should -Be 'entra-enable-pending'
        $script:User = New-GraphUser -Overrides @{ accountEnabled = $true }
        (Invoke-EntraWaitEnabled -In (New-CloudStepInput -Step 'Entra.WaitEnabled') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
    }

    It 'UsageLocation: already set → no PATCH' {
        (Invoke-EntraUsageLocation -In (New-CloudStepInput -Step 'Entra.UsageLocation') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Invoke-MgGraphRequest -ParameterFilter { $Method -eq 'PATCH' } -Times 0 -Exactly
    }

    It 'UsageLocation: missing → PATCH with the configured value' {
        $script:User = New-GraphUser -Overrides @{ usageLocation = $null }
        (Invoke-EntraUsageLocation -In (New-CloudStepInput -Step 'Entra.UsageLocation') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Invoke-MgGraphRequest -Times 1 -Exactly -ParameterFilter {
            $Method -eq 'PATCH' -and $Uri -eq 'v1.0/users/99999999-0000-0000-0000-000000000001' -and ($Body | ConvertFrom-Json).usageLocation -eq 'DE'
        }
    }

    It 'AssignLicense: assigns missing SKUs with disabled plans' {
        $in = New-CloudStepInput -Step 'Entra.AssignLicense'
        $in['cloud']['disabledServicePlans'] = @('YAMMER_ENTERPRISE')
        $result = Invoke-EntraAssignLicense -In $in -Context (New-StepContext -DryRun $false)

        $result.status | Should -Be 'done'
        # The filter also sees the GET calls (no body): only parse the body of the POST.
        Should -Invoke Invoke-MgGraphRequest -Times 1 -Exactly -ParameterFilter {
            if ($Method -ne 'POST' -or $Uri -notlike '*/assignLicense' -or [string]::IsNullOrEmpty($Body)) { return $false }
            $body = $Body | ConvertFrom-Json
            @($body.addLicenses).Count -eq 2 -and
            @($body.addLicenses | Where-Object { $_.skuId -eq $script:SpbId }).disabledPlans -contains 'p-yammer'
        }
    }

    It 'AssignLicense: already assigned → done without POST' {
        $script:User = New-GraphUser -Overrides @{ assignedLicenses = @(@{ skuId = $script:SpbId }, @{ skuId = $script:EvId }) }
        (Invoke-EntraAssignLicense -In (New-CloudStepInput -Step 'Entra.AssignLicense') -Context (New-StepContext -DryRun $false)).reason | Should -Be 'Lizenzen bereits zugewiesen.'
        Should -Invoke Invoke-MgGraphRequest -ParameterFilter { $Method -eq 'POST' } -Times 0 -Exactly
    }

    It 'AssignLicense: <Name> → <Code>' -TestCases @(
        @{ Name = 'no free license'; Setup = { $script:FreeEv = 0 }; Code = 'no-free-license' }
        @{ Name = 'unknown SKU'; Setup = { $script:In['cloud']['skus'] = @('SPB', 'GIBTSNICHT') }; Code = 'sku-not-found' }
        @{ Name = 'unknown service plan'; Setup = { $script:In['cloud']['disabledServicePlans'] = @('NOPE') }; Code = 'service-plan-not-found' }
        @{ Name = 'no usage location'; Setup = { $script:User = New-GraphUser -Overrides @{ usageLocation = '' } }; Code = 'usage-location-missing' }
    ) {
        $script:In = New-CloudStepInput -Step 'Entra.AssignLicense'
        & $Setup
        $result = Invoke-EntraAssignLicense -In $script:In -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be $Code
        Should -Invoke Invoke-MgGraphRequest -ParameterFilter { $Method -eq 'POST' } -Times 0 -Exactly
    }

    It 'AssignLicense: LicenseMode Group → skipped without connecting' {
        $in = New-CloudStepInput -Step 'Entra.AssignLicense'
        $in['cloud']['licenseMode'] = 'Group'
        $result = Invoke-EntraAssignLicense -In $in -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'skipped'
        $result.code | Should -Be 'license-mode-group'
        Should -Invoke Connect-MgGraph -Times 0 -Exactly
    }

    It 'WaitLicense: <Name> → <Status>/<Code>' -TestCases @(
        @{ Name = 'all active'; States = @(@{ skuId = '00000000-0000-0000-0000-00000000a5b1'; state = 'Active' }, @{ skuId = '00000000-0000-0000-0000-00000000e5e1'; state = 'Active' }); Status = 'done'; Code = $null }
        @{ Name = 'one pending'; States = @(@{ skuId = '00000000-0000-0000-0000-00000000a5b1'; state = 'Active' }, @{ skuId = '00000000-0000-0000-0000-00000000e5e1'; state = 'ActiveWithError' }); Status = 'waiting'; Code = 'license-pending' }
        @{ Name = 'error'; States = @(@{ skuId = '00000000-0000-0000-0000-00000000a5b1'; state = 'Error'; error = 'CountViolation' }, @{ skuId = '00000000-0000-0000-0000-00000000e5e1'; state = 'Active' }); Status = 'failed'; Code = 'license-error' }
        @{ Name = 'missing'; States = @(@{ skuId = '00000000-0000-0000-0000-00000000a5b1'; state = 'Active' }); Status = 'failed'; Code = 'license-not-assigned' }
    ) {
        $script:User = New-GraphUser -Overrides @{ licenseAssignmentStates = $States }
        $result = Invoke-EntraWaitLicense -In (New-CloudStepInput -Step 'Entra.WaitLicense') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be $Status
        if ($Code) { $result.code | Should -Be $Code } else { $result.code | Should -BeNullOrEmpty }
        if ($Code -eq 'license-error') { $result.reason | Should -Match 'CountViolation' }
    }
}

Describe 'EXO steps' {
    BeforeEach {
        Mock Connect-ExchangeOnline { }
        Mock Disconnect-ExchangeOnline { }
        Mock Get-EXOMailbox { [pscustomobject]@{ UserPrincipalName = $Identity } }
        Mock Get-CASMailbox { [pscustomobject]@{ OneWinNativeOutlookEnabled = $true } }
        Mock Set-CASMailbox { }
        Mock Get-MailboxPermission { @() }
        Mock Add-MailboxPermission { }
        Mock Get-RecipientPermission { @() }
        Mock Add-RecipientPermission { }
    }

    It 'WaitMailbox: missing → waiting, present → done; connects only with the needed command' {
        Mock Get-EXOMailbox { $null }
        (Invoke-ExoWaitMailbox -In (New-CloudStepInput -Step 'EXO.WaitMailbox') -Context (New-StepContext -DryRun $false)).code | Should -Be 'mailbox-pending'
        Mock Get-EXOMailbox { [pscustomobject]@{ UserPrincipalName = 'x' } }
        (Invoke-ExoWaitMailbox -In (New-CloudStepInput -Step 'EXO.WaitMailbox') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Connect-ExchangeOnline -ParameterFilter { @($CommandName) -join ',' -eq 'Get-EXOMailbox' -and $Organization -eq 'example.onmicrosoft.com' }
        Should -Invoke Disconnect-ExchangeOnline -Times 2 -Exactly
    }

    It 'DisableNewOutlook: sets false, idempotent when already false' {
        (Invoke-ExoDisableNewOutlook -In (New-CloudStepInput -Step 'EXO.DisableNewOutlook') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Set-CASMailbox -Times 1 -Exactly -ParameterFilter { $OneWinNativeOutlookEnabled -eq $false }

        Mock Get-CASMailbox { [pscustomobject]@{ OneWinNativeOutlookEnabled = $false } }
        (Invoke-ExoDisableNewOutlook -In (New-CloudStepInput -Step 'EXO.DisableNewOutlook') -Context (New-StepContext -DryRun $false)).reason | Should -Match 'bereits'
        Should -Invoke Set-CASMailbox -Times 1 -Exactly
    }

    It 'SharedMailboxes: adds FullAccess with AutoMapping and SendAs' {
        $result = Invoke-ExoSharedMailboxes -In (New-CloudStepInput -Step 'EXO.SharedMailboxes') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'done'
        Should -Invoke Add-MailboxPermission -Times 1 -Exactly -ParameterFilter { $Identity -eq 'team@example.test' -and $User -eq 'l.irion@example.test' -and $AccessRights -eq 'FullAccess' -and $AutoMapping -eq $true }
        Should -Invoke Add-RecipientPermission -Times 1 -Exactly -ParameterFilter { $Identity -eq 'team@example.test' -and $AccessRights -eq 'SendAs' }
    }

    It 'SharedMailboxes: existing rights → nothing added' {
        Mock Get-MailboxPermission { [pscustomobject]@{ AccessRights = @('FullAccess'); Deny = $false } }
        Mock Get-RecipientPermission { [pscustomobject]@{ AccessRights = @('SendAs'); AccessControlType = 'Allow' } }
        (Invoke-ExoSharedMailboxes -In (New-CloudStepInput -Step 'EXO.SharedMailboxes') -Context (New-StepContext -DryRun $false)).reason | Should -Be 'Berechtigungen bereits vorhanden.'
        Should -Invoke Add-MailboxPermission -Times 0 -Exactly
        Should -Invoke Add-RecipientPermission -Times 0 -Exactly
    }

    It 'SharedMailboxes: unknown shared mailbox → failed shared-mailbox-not-found' {
        Mock Get-EXOMailbox -ParameterFilter { $Identity -eq 'team@example.test' } { $null }
        $result = Invoke-ExoSharedMailboxes -In (New-CloudStepInput -Step 'EXO.SharedMailboxes') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'shared-mailbox-not-found'
    }
}

Describe 'Teams steps' {
    # Only our own layer is mocked (Connect-OnbTeams, Invoke-OnbTeams): with MicrosoftTeams installed,
    # Pester would mock the real cmdlets with their version-dependent parameter metadata.
    BeforeEach {
        Mock Connect-OnbTeams { [pscustomobject]@{ Account = $script:TestAppId; TenantId = $script:TestTenantId; Token = $script:TestToken } }
        Mock Disconnect-OnbTeams { }
        $script:CsUser = [pscustomobject]@{
            Identity = 'cs-user-1'; FeatureTypes = @('Teams', 'PhoneSystem'); LineUri = $null
            OnlineVoiceRoutingPolicy = $null; OnlineVoicemailPolicy = $null
        }
        $script:Assignment = $null
        $script:VoicemailSettings = [pscustomobject]@{ VoicemailEnabled = $false; PromptLanguage = 'en-US' }
        $script:CallingSettings = [pscustomobject]@{ IsUnansweredEnabled = $false; UnansweredDelay = '00:00:30'; UnansweredTargetType = $null; UnansweredTarget = $null }
        Mock Invoke-OnbTeams { }
        Mock Invoke-OnbTeams -ParameterFilter { $Command -eq 'Get-CsOnlineUser' } { $script:CsUser }
        Mock Invoke-OnbTeams -ParameterFilter { $Command -eq 'Get-CsPhoneNumberAssignment' } { $script:Assignment }
        Mock Invoke-OnbTeams -ParameterFilter { $Command -eq 'Get-CsOnlineVoicemailUserSettings' } { $script:VoicemailSettings }
        Mock Invoke-OnbTeams -ParameterFilter { $Command -eq 'Get-CsUserCallingSettings' } { $script:CallingSettings }
    }

    It 'WaitUser: without PhoneSystem → waiting' {
        $script:CsUser.FeatureTypes = @('Teams')
        $result = Invoke-TeamsWaitUser -In (New-CloudStepInput -Step 'Teams.WaitUser') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'waiting'
        $result.code | Should -Be 'teams-user-pending'
        Should -Invoke Disconnect-OnbTeams -Times 1 -Exactly
    }

    It 'WaitUser: with PhoneSystem → done' {
        (Invoke-TeamsWaitUser -In (New-CloudStepInput -Step 'Teams.WaitUser') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Connect-OnbTeams -Times 1 -Exactly -ParameterFilter { $Auth['tenantId'] -eq $script:TestTenantId -and $Auth['appId'] -eq $script:TestAppId }
    }

    It 'Phone: assigns the number with the configured type; idempotent via LineUri' {
        (Invoke-TeamsPhone -In (New-CloudStepInput -Step 'Teams.Phone') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Invoke-OnbTeams -Times 1 -Exactly -ParameterFilter {
            $Command -eq 'Set-CsPhoneNumberAssignment' -and $Parameters['Identity'] -eq 'l.irion@example.test' -and
            $Parameters['PhoneNumber'] -eq '+49123456781' -and $Parameters['PhoneNumberType'] -eq 'DirectRouting'
        }

        $script:CsUser.LineUri = 'tel:+49123456781;ext=781'
        (Invoke-TeamsPhone -In (New-CloudStepInput -Step 'Teams.Phone') -Context (New-StepContext -DryRun $false)).reason | Should -Match 'bereits'
        Should -Invoke Invoke-OnbTeams -Times 1 -Exactly -ParameterFilter { $Command -eq 'Set-CsPhoneNumberAssignment' }
    }

    It 'Phone: number of another user → failed number-in-use' {
        $script:Assignment = [pscustomobject]@{ AssignedPstnTargetId = 'someone-else' }
        $result = Invoke-TeamsPhone -In (New-CloudStepInput -Step 'Teams.Phone') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'number-in-use'
        Should -Invoke Invoke-OnbTeams -Times 0 -Exactly -ParameterFilter { $Command -eq 'Set-CsPhoneNumberAssignment' }
    }

    It 'Phone: Teams user missing → failed teams-user-not-found' {
        $script:CsUser = $null
        $result = Invoke-TeamsPhone -In (New-CloudStepInput -Step 'Teams.Phone') -Context (New-StepContext -DryRun $false)
        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'teams-user-not-found'
    }

    It '<Step> without extension → skipped no-extension, no connection' -TestCases @(
        @{ Step = 'Teams.WaitUser'; Handler = 'Invoke-TeamsWaitUser' }
        @{ Step = 'Teams.Phone'; Handler = 'Invoke-TeamsPhone' }
        @{ Step = 'Teams.VoiceRouting'; Handler = 'Invoke-TeamsVoiceRouting' }
        @{ Step = 'Teams.Voicemail'; Handler = 'Invoke-TeamsVoicemail' }
        @{ Step = 'Teams.Forwarding'; Handler = 'Invoke-TeamsForwarding' }
    ) {
        $in = New-CloudStepInput -Step $Step
        $in['cloud']['phoneE164'] = $null
        $result = & $Handler $in (New-StepContext -DryRun $false)
        $result.status | Should -Be 'skipped'
        $result.code | Should -Be 'no-extension'
        Should -Invoke Connect-OnbTeams -Times 0 -Exactly
    }

    It 'VoiceRouting: grants the configured policy once' {
        (Invoke-TeamsVoiceRouting -In (New-CloudStepInput -Step 'Teams.VoiceRouting') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Invoke-OnbTeams -Times 1 -Exactly -ParameterFilter { $Command -eq 'Grant-CsOnlineVoiceRoutingPolicy' -and $Parameters['PolicyName'] -eq 'Routing-Test' }
        $script:CsUser.OnlineVoiceRoutingPolicy = 'Routing-Test'
        Invoke-TeamsVoiceRouting -In (New-CloudStepInput -Step 'Teams.VoiceRouting') -Context (New-StepContext -DryRun $false) | Out-Null
        Should -Invoke Invoke-OnbTeams -Times 1 -Exactly -ParameterFilter { $Command -eq 'Grant-CsOnlineVoiceRoutingPolicy' }
    }

    It 'Voicemail: policy and settings' {
        (Invoke-TeamsVoicemail -In (New-CloudStepInput -Step 'Teams.Voicemail') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Invoke-OnbTeams -Times 1 -Exactly -ParameterFilter { $Command -eq 'Grant-CsOnlineVoicemailPolicy' -and $Parameters['PolicyName'] -eq 'Voicemail-Test' }
        Should -Invoke Invoke-OnbTeams -Times 1 -Exactly -ParameterFilter {
            $Command -eq 'Set-CsOnlineVoicemailUserSettings' -and $Parameters['VoicemailEnabled'] -eq $true -and
            $Parameters['PromptLanguage'] -eq 'de-DE' -and $Parameters['DefaultGreetingPromptOverwrite'] -eq ''
        }
    }

    It 'Voicemail: already set up → nothing changed' {
        $script:CsUser.OnlineVoicemailPolicy = 'Voicemail-Test'
        $script:VoicemailSettings = [pscustomobject]@{ VoicemailEnabled = $true; PromptLanguage = 'de-DE' }
        (Invoke-TeamsVoicemail -In (New-CloudStepInput -Step 'Teams.Voicemail') -Context (New-StepContext -DryRun $false)).reason | Should -Be 'Voicemail bereits eingerichtet.'
        Should -Invoke Invoke-OnbTeams -Times 0 -Exactly -ParameterFilter { $Command -in 'Grant-CsOnlineVoicemailPolicy', 'Set-CsOnlineVoicemailUserSettings' }
    }

    It 'Forwarding: sets unanswered forwarding; idempotent' {
        (Invoke-TeamsForwarding -In (New-CloudStepInput -Step 'Teams.Forwarding') -Context (New-StepContext -DryRun $false)).status | Should -Be 'done'
        Should -Invoke Invoke-OnbTeams -Times 1 -Exactly -ParameterFilter {
            $Command -eq 'Set-CsUserCallingSettings' -and $Parameters['IsUnansweredEnabled'] -eq $true -and $Parameters['UnansweredDelay'] -eq '00:00:20' -and
            $Parameters['UnansweredTargetType'] -eq 'singleTarget' -and $Parameters['UnansweredTarget'] -eq 'hotline@example.test'
        }
        $script:CallingSettings = [pscustomobject]@{ IsUnansweredEnabled = $true; UnansweredDelay = '00:00:20'; UnansweredTargetType = 'singleTarget'; UnansweredTarget = 'sip:Hotline@example.test' }
        (Invoke-TeamsForwarding -In (New-CloudStepInput -Step 'Teams.Forwarding') -Context (New-StepContext -DryRun $false)).reason | Should -Be 'Weiterleitung bereits eingerichtet.'
        Should -Invoke Invoke-OnbTeams -Times 1 -Exactly -ParameterFilter { $Command -eq 'Set-CsUserCallingSettings' }
    }

    It '<Step> with manualOnly → manualTask with ready command, no connection, no secrets' -TestCases @(
        @{ Step = 'Teams.Voicemail'; Handler = 'Invoke-TeamsVoicemail'; Command = 'Set-CsOnlineVoicemailUserSettings' }
        @{ Step = 'Teams.Forwarding'; Handler = 'Invoke-TeamsForwarding'; Command = 'Set-CsUserCallingSettings' }
    ) {
        $in = New-CloudStepInput -Step $Step
        $in['cloud']['manualOnly'] = $true
        $result = & $Handler $in (New-StepContext -DryRun $false)

        $result.status | Should -Be 'manualTask'
        $result.code | Should -Be 'manual-step'
        $result.reason | Should -Match ([regex]::Escape($Command))
        $result.reason | Should -Match "'l.irion@example.test'"
        Assert-NoConnectionDetails $result
        Should -Invoke Connect-OnbTeams -Times 0 -Exactly
        Should -Invoke Invoke-OnbTeams -Times 0 -Exactly
    }

    It 'failed Teams connect → cloud-auth-failed with exception type only' {
        Mock Connect-OnbTeams { throw [System.Management.Automation.ParameterBindingException]::new("token $script:TestToken tenant $script:TestTenantId") }
        $result = Invoke-StepHandler ${function:Invoke-TeamsVoiceRouting} (New-CloudStepInput -Step 'Teams.VoiceRouting') (New-StepContext -DryRun $false)

        $result.status | Should -Be 'failed'
        $result.code | Should -Be 'cloud-auth-failed'
        $result.reason | Should -Match 'ParameterBindingException'
        Assert-NoConnectionDetails $result
        Should -Invoke Invoke-OnbTeams -Times 0 -Exactly
    }

    It 'Invoke-OnbTeams accepts only the listed Teams commands' {
        { Invoke-OnbTeams -Command 'Remove-CsOnlineUser' } | Should -Throw
    }
}

Describe 'Dry-run (E5)' {
    BeforeEach {
        Mock Connect-MgGraph { $script:TestToken }
        Mock Disconnect-MgGraph { }
        Mock Invoke-MgGraphRequest -ParameterFilter { $Method -eq 'GET' -and $Uri -like 'v1.0/users/*' } { New-GraphUser -Overrides @{ usageLocation = $null } }
        Mock Invoke-MgGraphRequest -ParameterFilter { $Uri -eq 'v1.0/subscribedSkus' } { New-Skus }
        Mock Invoke-MgGraphRequest -ParameterFilter { $Method -in 'PATCH', 'POST' } { throw 'dry-run must not write' }
        Mock Connect-ExchangeOnline { [pscustomobject]@{ Token = $script:TestToken } }
        Mock Disconnect-ExchangeOnline { }
        Mock Get-EXOMailbox { [pscustomobject]@{ UserPrincipalName = $Identity } }
        Mock Get-CASMailbox { [pscustomobject]@{ OneWinNativeOutlookEnabled = $true } }
        Mock Set-CASMailbox { throw 'dry-run must not write' }
        Mock Get-MailboxPermission { @() }
        Mock Add-MailboxPermission { throw 'dry-run must not write' }
        Mock Get-RecipientPermission { @() }
        Mock Add-RecipientPermission { throw 'dry-run must not write' }
        Mock Connect-OnbTeams { [pscustomobject]@{ Account = $script:TestAppId; TenantId = $script:TestTenantId; Token = $script:TestToken } }
        Mock Disconnect-OnbTeams { }
        Mock Invoke-OnbTeams { throw 'dry-run must not write' }
        Mock Invoke-OnbTeams -ParameterFilter { $Command -eq 'Get-CsOnlineUser' } {
            [pscustomobject]@{ Identity = 'cs-1'; FeatureTypes = @('PhoneSystem'); LineUri = $null; OnlineVoiceRoutingPolicy = $null; OnlineVoicemailPolicy = $null }
        }
        Mock Invoke-OnbTeams -ParameterFilter { $Command -eq 'Get-CsPhoneNumberAssignment' } { $null }
        Mock Invoke-OnbTeams -ParameterFilter { $Command -eq 'Get-CsOnlineVoicemailUserSettings' } { [pscustomobject]@{ VoicemailEnabled = $false; PromptLanguage = 'en-US' } }
        Mock Invoke-OnbTeams -ParameterFilter { $Command -eq 'Get-CsUserCallingSettings' } {
            [pscustomobject]@{ IsUnansweredEnabled = $false; UnansweredDelay = '00:00:30'; UnansweredTargetType = $null; UnansweredTarget = $null }
        }
    }

    It '<Step> plans changes and leaks no token, tenant, app id or thumbprint' -TestCases @(
        @{ Step = 'Entra.UsageLocation'; Handler = 'Invoke-EntraUsageLocation' }
        @{ Step = 'Entra.AssignLicense'; Handler = 'Invoke-EntraAssignLicense' }
        @{ Step = 'EXO.DisableNewOutlook'; Handler = 'Invoke-ExoDisableNewOutlook' }
        @{ Step = 'EXO.SharedMailboxes'; Handler = 'Invoke-ExoSharedMailboxes' }
        @{ Step = 'Teams.Phone'; Handler = 'Invoke-TeamsPhone' }
        @{ Step = 'Teams.VoiceRouting'; Handler = 'Invoke-TeamsVoiceRouting' }
        @{ Step = 'Teams.Voicemail'; Handler = 'Invoke-TeamsVoicemail' }
        @{ Step = 'Teams.Forwarding'; Handler = 'Invoke-TeamsForwarding' }
    ) {
        $in = New-CloudStepInput -Step $Step -DryRun
        if ($Step -eq 'Entra.AssignLicense') {
            # usageLocation must be set for the license check; the mock above has none.
            Mock Invoke-MgGraphRequest -ParameterFilter { $Method -eq 'GET' -and $Uri -like 'v1.0/users/*' } { New-GraphUser }
        }

        $result = Invoke-StepHandler (Get-Command $Handler).ScriptBlock $in (New-StepContext -DryRun $true)

        $result.status | Should -Be 'done'
        $result.dryRun | Should -BeTrue
        @($result.plannedActions).Count | Should -BeGreaterThan 0
        Assert-NoConnectionDetails $result
    }

    It 'read-only steps in dry-run report the state without planned actions' {
        $result = Invoke-EntraWaitUser -In (New-CloudStepInput -Step 'Entra.WaitUser' -DryRun) -Context (New-StepContext -DryRun $true)
        $result.status | Should -Be 'done'
        @($result.plannedActions).Count | Should -Be 0
        Assert-NoConnectionDetails $result
    }
}
