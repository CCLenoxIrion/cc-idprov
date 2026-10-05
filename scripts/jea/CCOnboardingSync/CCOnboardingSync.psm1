<#
.SYNOPSIS
    JEA function of the endpoint "CC.Onboarding.Sync" on the Entra Connect server (CC01).

.DESCRIPTION
    Exactly one visible function without parameters: Start-OnbDeltaSync.
    "busy" (a sync cycle is already running) → status waiting.
    Compatible with Windows PowerShell 5.1.
#>

Set-StrictMode -Version 2.0

function New-OnbSyncResult {
    param([string] $Status, [string] $Code, [string] $Reason, [hashtable] $Output = @{})
    return [pscustomobject]@{ status = $Status; code = $Code; reason = $Reason; plannedActions = @(); output = [pscustomobject] $Output }
}

function Invoke-OnbSyncCycle {
    # Wrapper so Pester can mock the ADSync cmdlet.
    if (-not (Get-Command -Name Start-ADSyncSyncCycle -ErrorAction SilentlyContinue)) {
        Import-Module ADSync -ErrorAction Stop
    }
    Start-ADSyncSyncCycle -PolicyType Delta -ErrorAction Stop
}

function Start-OnbDeltaSync {
    [CmdletBinding()]
    param()

    try {
        $result = Invoke-OnbSyncCycle
        return New-OnbSyncResult -Status done -Reason 'Delta-Sync ausgelöst.' -Output @{ result = [string] $result.Result }
    }
    catch {
        # The message is only inspected, never returned.
        if ($_.Exception.Message -match 'busy|already in progress|bereits') {
            return New-OnbSyncResult -Status waiting -Code 'sync-busy' -Reason 'Synchronisierung läuft bereits (busy).'
        }
        return New-OnbSyncResult -Status failed -Code 'sync-error' -Reason ('Delta-Sync konnte nicht gestartet werden ({0}).' -f $_.Exception.GetType().Name)
    }
}

Export-ModuleMember -Function Start-OnbDeltaSync
