#Requires -Version 7.2
<#
.SYNOPSIS
    Manual test of a registered JEA endpoint (docs/DEPLOYMENT.md, "Manueller JEA-Testaufruf").
    Runs on the worker host (PowerShell 7) under the worker gMSA (scheduled task), never by the
    service itself. Lives in scripts/tools, not scripts/jea: nothing here is deployed to DC01/CC01.

.DESCRIPTION
    1. Opens a session and lists the visible commands (expected: exactly the endpoint functions
       plus the RestrictedRemoteServer defaults).
    2. CC.Onboarding (file server DC01): New-OnbHomeFolder and New-OnbHomeShare via the same
       implicit-remoting path as the step scripts (Invoke-JeaFunction), always with -DryRun,
       plus negative calls (invalid sam, principal not in the allowlist) that must be rejected.
       CC.Onboarding.Logon (DC03): Set-OnbLogonScript with -DryRun, plus a wrong hash.
    3. CC.Onboarding.Sync: lists commands only; Start-OnbDeltaSync runs only with -TriggerSync
       (it starts a real delta sync – there is no dry-run on the endpoint).

    Output: one JSON document on stdout and, with -OutFile, in that file.

.EXAMPLE
    pwsh -NoProfile -File Test-OnboardingJea.ps1 -ComputerName DC01 -ConfigurationName CC.Onboarding -OutFile C:\Temp\jea-dc.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ComputerName,
    [Parameter(Mandatory)] [ValidateSet('CC.Onboarding', 'CC.Onboarding.Logon', 'CC.Onboarding.Sync')] [string] $ConfigurationName,
    [ValidateSet('Modify', 'FullControl')] [string] $UserRight = 'Modify',
    [string[]] $AdditionalAces = @('SYSTEM=FullControl', 'BUILTIN\Administrators=FullControl'),
    [ValidatePattern('^[a-z0-9]{1,20}$')] [string] $Sam = 'jeatest',
    [switch] $TriggerSync,
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force

$report = [ordered]@{
    computer        = $ComputerName
    configuration   = $ConfigurationName
    identity        = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    visibleCommands = @()
    calls           = @()
}

function Add-Call {
    param([string] $Name, [string] $Expectation, [scriptblock] $Call)
    $entry = [ordered]@{ call = $Name; expectation = $Expectation }
    try {
        $result = & $Call
        $entry.status = $result.status
        $entry.reason = $result.reason
        $entry.plannedActions = @($result.plannedActions)
    }
    catch {
        $entry.status = 'error'
        $entry.reason = $_.Exception.Message
    }
    $script:report.calls += [pscustomobject] $entry
}

# 1. Visible commands.
$session = $null
try {
    $session = New-PSSession -ComputerName $ComputerName -ConfigurationName $ConfigurationName
    $report.visibleCommands = @(Invoke-Command -Session $session -ScriptBlock { Get-Command } | ForEach-Object Name | Sort-Object)
}
catch {
    $report.visibleCommands = @("FEHLER: $($_.Exception.Message)")
}
finally {
    if ($null -ne $session) { Remove-PSSession -Session $session }
}

$jea = @{ ComputerName = $ComputerName; ConfigurationName = $ConfigurationName }

if ($ConfigurationName -eq 'CC.Onboarding') {
    # 2. Dry-run calls (nothing is changed on DC01).
    Add-Call 'New-OnbHomeFolder (Dry-Run)' 'done/waiting mit geplanten Aktionen' {
        Invoke-JeaFunction @jea -FunctionName 'New-OnbHomeFolder' -Parameters @{ Sam = $Sam; UserRight = $UserRight; AdditionalAces = $AdditionalAces; DryRun = $true }
    }
    Add-Call 'New-OnbHomeShare (Dry-Run)' 'done/waiting mit geplanten Aktionen' {
        Invoke-JeaFunction @jea -FunctionName 'New-OnbHomeShare' -Parameters @{ Sam = $Sam; DryRun = $true }
    }

    # Negative calls: must be rejected by the endpoint (failed or error), never executed.
    Add-Call 'New-OnbHomeFolder ungültige sam' 'abgelehnt' {
        Invoke-JeaFunction @jea -FunctionName 'New-OnbHomeFolder' -Parameters @{ Sam = '..\admin'; UserRight = $UserRight; DryRun = $true }
    }
    Add-Call 'New-OnbHomeFolder Principal außerhalb der Allowlist' 'abgelehnt (ace-not-allowed)' {
        Invoke-JeaFunction @jea -FunctionName 'New-OnbHomeFolder' -Parameters @{ Sam = $Sam; UserRight = $UserRight; AdditionalAces = @('Everyone=FullControl'); DryRun = $true }
    }
}
elseif ($ConfigurationName -eq 'CC.Onboarding.Logon') {
    # Dry-run calls (nothing is written to NETLOGON on DC03).
    $bytes = [System.Text.Encoding]::ASCII.GetBytes("@echo off`r`nREM JEA-Test`r`n")
    $base64 = [Convert]::ToBase64String($bytes)
    $sha256 = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    Add-Call 'Set-OnbLogonScript (Dry-Run)' 'done mit geplanter Aktion' {
        Invoke-JeaFunction @jea -FunctionName 'Set-OnbLogonScript' -Parameters @{ Sam = $Sam; ContentBase64 = $base64; Sha256 = $sha256; DryRun = $true }
    }
    Add-Call 'Set-OnbLogonScript falscher Hash' 'abgelehnt' {
        Invoke-JeaFunction @jea -FunctionName 'Set-OnbLogonScript' -Parameters @{ Sam = $Sam; ContentBase64 = $base64; Sha256 = ('0' * 64); DryRun = $true }
    }
}
elseif ($TriggerSync) {
    Add-Call 'Start-OnbDeltaSync' 'done oder waiting (Sync läuft bereits)' {
        Invoke-JeaFunction @jea -FunctionName 'Start-OnbDeltaSync'
    }
}

$json = [pscustomobject] $report | ConvertTo-Json -Depth 6
if ($OutFile) { Set-Content -Path $OutFile -Value $json -Encoding utf8 }
$json
