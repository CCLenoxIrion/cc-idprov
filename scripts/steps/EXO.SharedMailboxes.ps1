#Requires -Version 7.2
<#
.SYNOPSIS
    EXO.SharedMailboxes (SPEC §7): Grants FullAccess (with AutoMapping) and SendAs on the department shared mailboxes.
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Test-OnbFullAccess {
    param([string] $Mailbox, [string] $User)
    $permissions = @(Get-MailboxPermission -Identity $Mailbox -User $User -ErrorAction Stop)
    return @($permissions | Where-Object { -not $_.Deny -and @($_.AccessRights) -contains 'FullAccess' }).Count -gt 0
}

function Test-OnbSendAs {
    param([string] $Mailbox, [string] $User)
    $permissions = @(Get-RecipientPermission -Identity $Mailbox -Trustee $User -ErrorAction Stop)
    return @($permissions | Where-Object { [string] $_.AccessControlType -ne 'Deny' -and @($_.AccessRights) -contains 'SendAs' }).Count -gt 0
}

function Invoke-ExoSharedMailboxes {
    <#
        An existing FullAccess permission is accepted regardless of AutoMapping: changing it would
        mean remove + re-add (interrupting access). Nothing is ever removed.
    #>
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    $upn = [string] $cloud['upn']
    $mailboxes = @($cloud['sharedMailboxes'] | Where-Object { $null -ne $_ })
    if ($mailboxes.Count -eq 0) {
        return New-StepResult -Context $Context -Status done -Reason 'Keine Freigabepostfächer konfiguriert.'
    }

    $commands = @('Get-EXOMailbox', 'Get-MailboxPermission', 'Add-MailboxPermission', 'Get-RecipientPermission', 'Add-RecipientPermission')
    Invoke-OnbCloudSession -Service Exchange -In $In -CommandName $commands -Body {
        if ($null -eq (Get-OnbMailbox -Identity $upn)) {
            return New-StepResult -Context $Context -Status failed -Code 'mailbox-not-found' -Reason 'Postfach des Benutzers nicht gefunden.'
        }

        $added = [System.Collections.Generic.List[string]]::new()
        foreach ($entry in $mailboxes) {
            $mailbox = [string] $entry['mailbox']
            if ($null -eq (Get-OnbMailbox -Identity $mailbox)) {
                return New-StepResult -Context $Context -Status failed -Code 'shared-mailbox-not-found' -Reason ("Freigabepostfach '{0}' nicht gefunden." -f $mailbox)
            }

            if ([bool] $entry['fullAccess'] -and -not (Test-OnbFullAccess -Mailbox $mailbox -User $upn)) {
                $autoMapping = [bool] $entry['autoMapping']
                Invoke-StepChange -Context $Context -Description ("FullAccess auf '{0}' (AutoMapping {1})" -f $mailbox, $autoMapping) -Action {
                    Add-MailboxPermission -Identity $mailbox -User $upn -AccessRights FullAccess -AutoMapping $autoMapping -Confirm:$false -ErrorAction Stop | Out-Null
                } | Out-Null
                $added.Add("$mailbox FullAccess")
            }

            if ([bool] $entry['sendAs'] -and -not (Test-OnbSendAs -Mailbox $mailbox -User $upn)) {
                Invoke-StepChange -Context $Context -Description ("SendAs auf '{0}'" -f $mailbox) -Action {
                    Add-RecipientPermission -Identity $mailbox -Trustee $upn -AccessRights SendAs -Confirm:$false -ErrorAction Stop | Out-Null
                } | Out-Null
                $added.Add("$mailbox SendAs")
            }
        }

        $reason = if ($added.Count -eq 0) { 'Berechtigungen bereits vorhanden.' } else { 'Hinzugefügt: {0}.' -f ($added -join ', ') }
        New-StepResult -Context $Context -Status done -Reason $reason
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-ExoSharedMailboxes}
}
