#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Registers the JEA endpoint for onboarding on DC01 ("CC.Onboarding") or CC01
    ("CC.Onboarding.Sync"). Run by an administrator on the target server (DEPLOYMENT.md).

.DESCRIPTION
    1. Copies the module (incl. RoleCapabilities) to %ProgramFiles%\WindowsPowerShell\Modules.
    2. Creates the session configuration file via New-PSSessionConfigurationFile.
    3. Validates it (Test-PSSessionConfigurationFile) and registers it.

    Run-as variants (DEPLOYMENT.md, ZU VERIFIZIEREN):
      -RunAs VirtualAccount : virtual account limited by -RunAsGroup
                              (on DCs a virtual account is Domain Admin by default!)
      -RunAs Gmsa           : dedicated endpoint gMSA (-EndpointGmsa) with only the needed rights

.EXAMPLE
    .\Register-OnboardingJea.ps1 -Role Dc -WorkerGmsa 'CC\svc-onboard$' -RunAs Gmsa -EndpointGmsa 'CC\svc-onbjea-dc$'

.EXAMPLE
    .\Register-OnboardingJea.ps1 -Role Sync -WorkerGmsa 'CC\svc-onboard$' -RunAs VirtualAccount -RunAsGroup 'ADSyncOperators'
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [ValidateSet('Dc', 'Sync')] [string] $Role,
    [Parameter(Mandatory)] [ValidatePattern('^[^\\]+\\[^\\]+\$$')] [string] $WorkerGmsa,
    [Parameter(Mandatory)] [ValidateSet('VirtualAccount', 'Gmsa')] [string] $RunAs,
    [string] $RunAsGroup,
    [string] $EndpointGmsa,
    [string] $TranscriptDirectory = 'C:\ProgramData\CCOnboarding\Transcripts'
)

$ErrorActionPreference = 'Stop'

$settings = @{
    Dc   = @{ Module = 'CCOnboarding'; Capability = 'OnboardingDc'; Name = 'CC.Onboarding' }
    Sync = @{ Module = 'CCOnboardingSync'; Capability = 'OnboardingSync'; Name = 'CC.Onboarding.Sync' }
}[$Role]

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
        if (-not $RunAsGroup) { throw '-RunAsGroup ist für -RunAs VirtualAccount erforderlich.' }
        $configuration['RunAsVirtualAccount'] = $true
        $configuration['RunAsVirtualAccountGroups'] = @($RunAsGroup)
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
