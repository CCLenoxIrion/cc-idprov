#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Registers one JEA endpoint for onboarding (DEPLOYMENT.md §4), run by an administrator on the
    target server:
      -Role Home  : "CC.Onboarding"       on the file server DC01 (member server)
      -Role Logon : "CC.Onboarding.Logon" on the domain controller DC03 (only -RunAs Gmsa)
      -Role Sync  : "CC.Onboarding.Sync"  on the Entra Connect server CC01

.DESCRIPTION
    1. Copies the module (incl. RoleCapabilities) to %ProgramFiles%\WindowsPowerShell\Modules.
    2. Creates the session configuration file via New-PSSessionConfigurationFile.
    3. Validates it (Test-PSSessionConfigurationFile) and registers it.

    Run-as variants (DEPLOYMENT.md §4.2):
      -RunAs VirtualAccount : virtual account; on a member server local administrator, optionally
                              limited to -RunAsGroup. Refused for -Role Logon: on a DC a virtual
                              account is Domain Admin.
      -RunAs Gmsa           : dedicated endpoint gMSA (-EndpointGmsa) with only the needed rights

.EXAMPLE
    .\Register-OnboardingJea.ps1 -Role Home -WorkerGmsa 'CC\svc-onboard$' -RunAs VirtualAccount

.EXAMPLE
    .\Register-OnboardingJea.ps1 -Role Logon -WorkerGmsa 'CC\svc-onboard$' -RunAs Gmsa -EndpointGmsa 'CC\svc-onbjea-logon$'

.EXAMPLE
    .\Register-OnboardingJea.ps1 -Role Sync -WorkerGmsa 'CC\svc-onboard$' -RunAs VirtualAccount -RunAsGroup 'ADSyncOperators'
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [ValidateSet('Home', 'Logon', 'Sync')] [string] $Role,
    [Parameter(Mandatory)] [ValidatePattern('^[^\\]+\\[^\\]+\$$')] [string] $WorkerGmsa,
    [Parameter(Mandatory)] [ValidateSet('VirtualAccount', 'Gmsa')] [string] $RunAs,
    [string] $RunAsGroup,
    [string] $EndpointGmsa,
    [string] $TranscriptDirectory = 'C:\ProgramData\CCOnboarding\Transcripts'
)

$ErrorActionPreference = 'Stop'

$settings = @{
    Home  = @{ Module = 'CCOnboarding'; Capability = 'OnboardingHome'; Name = 'CC.Onboarding' }
    Logon = @{ Module = 'CCOnboardingLogon'; Capability = 'OnboardingLogon'; Name = 'CC.Onboarding.Logon' }
    Sync  = @{ Module = 'CCOnboardingSync'; Capability = 'OnboardingSync'; Name = 'CC.Onboarding.Sync' }
}[$Role]

if ($Role -eq 'Logon' -and $RunAs -ne 'Gmsa') {
    throw 'CC.Onboarding.Logon läuft auf einem Domänencontroller: nur -RunAs Gmsa (ein Virtual Account wäre Domain Admin).'
}

# 1. Module with role capability. Target folder must be writable by administrators only.
$source = Join-Path $PSScriptRoot $settings.Module
$target = Join-Path $env:ProgramFiles "WindowsPowerShell\Modules\$($settings.Module)"
if ($PSCmdlet.ShouldProcess($target, 'Modul kopieren')) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item -Path $source -Destination $target -Recurse
}

# 2. Session configuration file.
$configuration = @{
    Path                = Join-Path $env:TEMP "$($settings.Name).pssc"
    SessionType         = 'RestrictedRemoteServer'
    LanguageMode        = 'NoLanguage'
    TranscriptDirectory = $TranscriptDirectory
    RoleDefinitions     = @{ $WorkerGmsa = @{ RoleCapabilities = $settings.Capability } }
}

switch ($RunAs) {
    'VirtualAccount' {
        # Without -RunAsGroup the virtual account is a local administrator (fine on the member
        # server DC01); with -RunAsGroup it is limited to that group (e.g. ADSyncOperators on CC01).
        $configuration['RunAsVirtualAccount'] = $true
        if ($RunAsGroup) { $configuration['RunAsVirtualAccountGroups'] = @($RunAsGroup) }
    }
    'Gmsa' {
        if (-not $EndpointGmsa) { throw '-EndpointGmsa ist für -RunAs Gmsa erforderlich.' }
        $configuration['GroupManagedServiceAccount'] = $EndpointGmsa
    }
}

New-Item -ItemType Directory -Path $TranscriptDirectory -Force | Out-Null
New-PSSessionConfigurationFile @configuration

# 3. Validate and register.
if (-not (Test-PSSessionConfigurationFile -Path $configuration.Path)) {
    throw "Session-Konfiguration $($configuration.Path) ist ungültig."
}

if ($PSCmdlet.ShouldProcess($settings.Name, 'Session-Konfiguration registrieren')) {
    if (Get-PSSessionConfiguration -Name $settings.Name -ErrorAction SilentlyContinue) {
        Unregister-PSSessionConfiguration -Name $settings.Name -Force
    }
    Register-PSSessionConfiguration -Name $settings.Name -Path $configuration.Path -Force
}

Write-Output "Registriert: $($settings.Name). Test siehe docs/DEPLOYMENT.md (manueller JEA-Testaufruf)."
