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
                dcComputer            = 'DC01'
                dcConfigurationName   = 'CC.Onboarding'
                syncComputer          = 'CC01'
                syncConfigurationName = 'CC.Onboarding.Sync'
            }
        }
        logonScript         = @{
            contentBase64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("net use h: /del /y`r`n"))
            sha256        = ''
        }
    }
    if ($Step -eq 'AD.CreateUser') { $in['initialPassword'] = $Password }
    return $in
}
