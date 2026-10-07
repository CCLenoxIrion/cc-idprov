<#
.SYNOPSIS
    Stub commands so Pester can mock AD/SMB/ADSync cmdlets on machines without these modules.
    Real modules (if installed) are not touched: stubs are only defined when missing.
    Every stub throws – a test that forgets a mock fails instead of touching a real system.
#>

$stubs = @{
    'Get-ADUser'                 = 'param($Identity, $LDAPFilter, $Properties, $ErrorAction)'
    'Get-ADObject'               = 'param($LDAPFilter, $SearchBase, $SearchScope, $Properties, $ErrorAction)'
    'Get-ADGroup'                = 'param($Identity, $ErrorAction)'
    'New-ADUser'                 = 'param($Name, $SamAccountName, $UserPrincipalName, $GivenName, $Surname, $DisplayName, $EmailAddress, $Department, $Company, $ScriptPath, $Path, $Manager, $AccountPassword, $Enabled, $ChangePasswordAtLogon, $OtherAttributes, $OfficePhone, [switch] $PassThru, $ErrorAction)'
    'Set-ADUser'                 = 'param($Identity, $Replace, $Manager, $ErrorAction)'
    'Set-ADAccountPassword'      = 'param($Identity, $NewPassword, [switch] $Reset, $ErrorAction)'
    'Add-ADGroupMember'          = 'param($Identity, $Members, $ErrorAction)'
    'Enable-ADAccount'           = 'param($Identity, $ErrorAction)'
    'Get-SmbShare'               = 'param($Name, $ErrorAction)'
    'Get-SmbShareAccess'         = 'param($Name, $ErrorAction)'
    'New-SmbShare'               = 'param($Name, $Path, $ChangeAccess, $FullAccess, $ErrorAction)'
    'Grant-SmbShareAccess'       = 'param($Name, $AccountName, $AccessRight, [switch] $Force, $ErrorAction)'
    'Start-ADSyncSyncCycle'      = 'param($PolicyType, $ErrorAction)'
    # Cloud (phase 4b)
    'Connect-MgGraph'                    = 'param($ClientId, $TenantId, $CertificateThumbprint, [switch] $NoWelcome, $ErrorAction)'
    'Disconnect-MgGraph'                 = 'param($ErrorAction)'
    'Invoke-MgGraphRequest'              = 'param($Method, $Uri, $Body, $ContentType, $Headers, $OutputType, $ErrorAction)'
    'Connect-ExchangeOnline'             = 'param($AppId, $CertificateThumbprint, $Organization, [switch] $ShowBanner, $CommandName, $ErrorAction)'
    'Disconnect-ExchangeOnline'          = 'param([switch] $Confirm, $ErrorAction)'
    'Get-EXOMailbox'                     = 'param($Identity, $ErrorAction)'
    'Get-CASMailbox'                     = 'param($Identity, $ErrorAction)'
    'Set-CASMailbox'                     = 'param($Identity, $OneWinNativeOutlookEnabled, $ErrorAction)'
    'Get-MailboxPermission'              = 'param($Identity, $User, $ErrorAction)'
    'Add-MailboxPermission'              = 'param($Identity, $User, $AccessRights, $AutoMapping, [switch] $Confirm, $ErrorAction)'
    'Get-RecipientPermission'            = 'param($Identity, $Trustee, $ErrorAction)'
    'Add-RecipientPermission'            = 'param($Identity, $Trustee, $AccessRights, [switch] $Confirm, $ErrorAction)'
    'Connect-MicrosoftTeams'             = 'param($ApplicationId, $CertificateThumbprint, $TenantId, $ErrorAction)'
    'Disconnect-MicrosoftTeams'          = 'param($ErrorAction)'
    'Get-CsOnlineUser'                   = 'param($Identity, $ErrorAction)'
    'Get-CsPhoneNumberAssignment'        = 'param($TelephoneNumber, $ErrorAction)'
    'Set-CsPhoneNumberAssignment'        = 'param($Identity, $PhoneNumber, $PhoneNumberType, $ErrorAction)'
    'Grant-CsOnlineVoiceRoutingPolicy'   = 'param($Identity, $PolicyName, $ErrorAction)'
    'Grant-CsOnlineVoicemailPolicy'      = 'param($Identity, $PolicyName, $ErrorAction)'
    'Get-CsOnlineVoicemailUserSettings'  = 'param($Identity, $ErrorAction)'
    'Set-CsOnlineVoicemailUserSettings'  = 'param($Identity, $VoicemailEnabled, $PromptLanguage, $DefaultGreetingPromptOverwrite, $ErrorAction)'
    'Get-CsUserCallingSettings'          = 'param($Identity, $ErrorAction)'
    'Set-CsUserCallingSettings'          = 'param($Identity, $IsUnansweredEnabled, $UnansweredDelay, $UnansweredTargetType, $UnansweredTarget, $ErrorAction)'
}

foreach ($name in $stubs.Keys) {
    if (-not (Get-Command -Name $name -ErrorAction SilentlyContinue)) {
        $body = "[CmdletBinding()] $($stubs[$name]) throw 'Stub $name ohne Mock aufgerufen.'"
        Set-Item -Path "function:global:$name" -Value ([scriptblock]::Create($body))
    }
}

if (-not ('Microsoft.ActiveDirectory.Management.ADIdentityNotFoundException' -as [type])) {
    Add-Type -TypeDefinition @'
namespace Microsoft.ActiveDirectory.Management {
    public class ADIdentityNotFoundException : System.Exception {
        public ADIdentityNotFoundException() : base("not found") {}
        public ADIdentityNotFoundException(string message) : base(message) {}
    }
    public class ADPasswordComplexityException : System.Exception {
        public ADPasswordComplexityException(string message) : base(message) {}
    }
}
'@
}

if (-not ('OnbTest.FakeGraphHttpException' -as [type])) {
    # Shape of the Graph SDK's HTTP error: Exception.Response.StatusCode (ZU VERIFIZIEREN gegen das Modul).
    Add-Type -TypeDefinition @'
namespace OnbTest {
    public class FakeGraphResponse { public System.Net.HttpStatusCode StatusCode { get; set; } }
    public class FakeGraphHttpException : System.Exception {
        public FakeGraphHttpException(int status, string message) : base(message) {
            Response = new FakeGraphResponse { StatusCode = (System.Net.HttpStatusCode)status };
        }
        public FakeGraphResponse Response { get; private set; }
    }
}
'@
}

function Invoke-StepHandler {
    <# Runs a step handler like Invoke-StepMain does: an exception becomes a sanitized failed result. #>
    param([scriptblock] $Handler, [hashtable] $In, $Context)
    try {
        & $Handler $In $Context
    }
    catch {
        $safe = Get-SafeError $_
        New-StepResult -Context $Context -Status $safe.status -Code $safe.code -Reason $safe.reason
    }
}

function New-TestStepInput {
    <# Builds a step input hashtable like the C# StepScriptInputBuilder. #>
    param(
        [string] $Step = 'AD.CreateUser',
        [switch] $DryRun,
        [switch] $Force,
        [string] $Password = 'Geheim-Start!2026',
        [string] $DirectoryObjectGuid
    )

    $in = @{
        step                = $Step
        requestId           = '6f1c2e3d-4b5a-4c6d-8e9f-0a1b2c3d4e5f'
        dryRun              = [bool] $DryRun
        force               = [bool] $Force
        managerObjectGuid   = '0f3c1a52-1b8e-4f5a-9c2d-000000000001'
        directoryObjectGuid = $DirectoryObjectGuid
        identity            = @{
            sam                  = 'lirion'
            upn                  = 'l.irion@example.test'
            mail                 = 'l.irion@example.test'
            givenName            = 'Lenox'
            sn                   = 'Irion'
            displayName          = 'Lenox Irion'
            department           = 'Vertrieb'
            company              = 'Example GmbH'
            ouDn                 = 'OU=Users,OU=Technical,DC=example,DC=test'
            scriptPath           = 'lirion.bat'
            telephoneNumber      = '+49 1234 5678-12'
            proxyAddresses       = @('SMTP:l.irion@example.test', 'smtp:l.irion@example.com')
            additionalAttributes = @{ extensionAttribute1 = 'Dr.' }
        }
        config              = @{
            requestIdAttribute = 'extensionAttribute15'
            groups             = @('GG-Vertrieb')
            jea                = @{
                homeComputer           = 'DC01'
                homeConfigurationName  = 'CC.Onboarding'
                logonComputer          = 'DC03'
                logonConfigurationName = 'CC.Onboarding.Logon'
                syncComputer          = 'CC01'
                syncConfigurationName = 'CC.Onboarding.Sync'
            }
            home               = @{
                userRight      = 'Modify'
                additionalAces = @(
                    @{ principal = 'SYSTEM'; right = 'FullControl' }
                    @{ principal = 'BUILTIN\Administrators'; right = 'FullControl' }
                )
            }
        }
        logonScript         = @{
            contentBase64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("net use h: /del /y`r`n"))
            sha256        = ''
        }
    }
    if ($Step -eq 'AD.CreateUser') { $in['initialPassword'] = $Password }
    if ($Step -match '^(Entra|EXO|Teams)\.') { $in['cloud'] = New-TestCloudInput }
    return $in
}

# Values that must never appear in any step output (E5).
$script:TestTenantId = '11111111-1111-1111-1111-111111111111'
$script:TestAppId = '22222222-2222-2222-2222-222222222222'
$script:TestThumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'
$script:TestToken = 'eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.GEHEIMER-TOKEN'

function New-TestCloudInput {
    <# Cloud block like the C# StepScriptJson.BuildCloud. #>
    return @{
        auth                       = @{
            tenantId              = $script:TestTenantId
            appId                 = $script:TestAppId
            certificateThumbprint = $script:TestThumbprint
            organization          = 'example.onmicrosoft.com'
        }
        upn                        = 'l.irion@example.test'
        usageLocation              = 'DE'
        licenseMode                = 'Direct'
        skus                       = @('SPB', 'MCOEV')
        disabledServicePlans       = @()
        phoneE164                  = '+49123456781'
        oneWinNativeOutlookEnabled = $false
        sharedMailboxes            = @(@{ mailbox = 'team@example.test'; fullAccess = $true; autoMapping = $true; sendAs = $true })
        teams                      = @{
            voiceRoutingPolicy = 'Routing-Test'
            voicemailPolicy    = 'Voicemail-Test'
            promptLanguage     = 'de-DE'
            phoneNumberType    = 'DirectRouting'
            voicemail          = $true
            forward            = @{ enabled = $true; delaySeconds = 20; targetType = 'singleTarget'; target = 'hotline@example.test' }
        }
        manualOnly                 = $false
    }
}
