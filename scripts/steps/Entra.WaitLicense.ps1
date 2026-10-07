#Requires -Version 7.2
<#
.SYNOPSIS
    Entra.WaitLicense (SPEC §7): Waits until the licenses are active (licenseAssignmentStates).
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Invoke-EntraWaitLicense {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    Invoke-OnbCloudSession -Service Graph -In $In -Body {
        $own = Get-OwnCloudUser -In $In -Context $Context
        if ($own.ContainsKey('Result')) { return $own.Result }
        $states = @($own.User['licenseAssignmentStates'] | Where-Object { $null -ne $_ })

        $expected = [System.Collections.Generic.List[object]]::new()
        if ([string] $cloud['licenseMode'] -eq 'Group') {
            # Group-based licensing: any license, all active, none in error.
            foreach ($state in $states) { $expected.Add($state) }
            if ($expected.Count -eq 0) {
                return New-StepResult -Context $Context -Status waiting -Code 'license-pending' -Reason 'Noch keine Lizenz über die Lizenzgruppe zugewiesen.'
            }
        }
        else {
            $subscribed = Get-OnbSubscribedSkus
            foreach ($partNumber in @($cloud['skus'])) {
                $sku = @($subscribed | Where-Object { [string] $_['skuPartNumber'] -eq [string] $partNumber }) | Select-Object -First 1
                $match = if ($null -eq $sku) { @() } else { @($states | Where-Object { [string] $_['skuId'] -eq [string] $sku['skuId'] }) }
                if ($match.Count -eq 0) {
                    return New-StepResult -Context $Context -Status failed -Code 'license-not-assigned' -Reason ("Lizenz '{0}' ist nicht zugewiesen." -f $partNumber)
                }
                foreach ($state in $match) { $expected.Add($state) }
            }
        }

        $errors = @($expected | Where-Object { [string] $_['state'] -eq 'Error' })
        if ($errors.Count -gt 0) {
            # 'error' is an enum value such as CountViolation or MutuallyExclusiveViolation.
            $kinds = @($errors | ForEach-Object { [string] $_['error'] } | Where-Object { $_ -match '^[A-Za-z]{1,64}$' } | Select-Object -Unique) -join ', '
            return New-StepResult -Context $Context -Status failed -Code 'license-error' -Reason ('Lizenzzuweisung fehlerhaft: {0}.' -f $kinds)
        }

        if (@($expected | Where-Object { [string] $_['state'] -ne 'Active' }).Count -gt 0) {
            return New-StepResult -Context $Context -Status waiting -Code 'license-pending' -Reason 'Lizenz noch nicht aktiv.'
        }

        New-StepResult -Context $Context -Status done -Reason 'Lizenzen aktiv.'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-EntraWaitLicense}
}
