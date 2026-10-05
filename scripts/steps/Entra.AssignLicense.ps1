#Requires -Version 7.2
<#
.SYNOPSIS
    Entra.AssignLicense (SPEC §7): Assigns the department SKUs directly (LicenseMode Direct); free licenses are checked first.
    Input JSON on stdin, result JSON on stdout (DECISIONS X3).
#>
[CmdletBinding()]
param()

Import-Module ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'Onboarding.Step.psm1')) -Force
. ([System.IO.Path]::Combine($PSScriptRoot, '..', 'common', 'CloudHelpers.ps1'))

function Find-OnbSku {
    param([object[]] $Skus, [string] $PartNumber)
    return @($Skus | Where-Object { [string] $_['skuPartNumber'] -eq $PartNumber }) | Select-Object -First 1
}

function Invoke-EntraAssignLicense {
    param([Parameter(Mandatory)] [hashtable] $In, [Parameter(Mandatory)] $Context)

    $cloud = Get-CloudInput $In
    if ([string] $cloud['licenseMode'] -eq 'Group') {
        return New-StepResult -Context $Context -Status skipped -Code 'license-mode-group' -Reason 'LicenseMode = Group: Lizenz kommt über die Lizenzgruppe.'
    }

    Invoke-OnbCloudSession -Service Graph -In $In -Body {
        $own = Get-OwnCloudUser -In $In -Context $Context
        if ($own.ContainsKey('Result')) { return $own.Result }
        $user = $own.User
        if ([string]::IsNullOrEmpty([string] $user['usageLocation'])) {
            return New-StepResult -Context $Context -Status failed -Code 'usage-location-missing' -Reason 'Benutzer hat keine usageLocation (Entra.UsageLocation zuerst).'
        }

        $subscribed = Get-OnbSubscribedSkus
        $wanted = [System.Collections.Generic.List[object]]::new()
        foreach ($partNumber in @($cloud['skus'])) {
            $sku = Find-OnbSku -Skus $subscribed -PartNumber ([string] $partNumber)
            if ($null -eq $sku) {
                return New-StepResult -Context $Context -Status failed -Code 'sku-not-found' -Reason ("SKU '{0}' ist im Tenant nicht vorhanden." -f $partNumber)
            }
            $wanted.Add($sku)
        }

        $disabledNames = @($cloud['disabledServicePlans'] | ForEach-Object { [string] $_ })
        foreach ($planName in $disabledNames) {
            $known = @($wanted | Where-Object { @($_['servicePlans'] | Where-Object { [string] $_['servicePlanName'] -eq $planName }).Count -gt 0 })
            if ($known.Count -eq 0) {
                return New-StepResult -Context $Context -Status failed -Code 'service-plan-not-found' -Reason ("Service-Plan '{0}' ist in keiner der SKUs enthalten." -f $planName)
            }
        }

        $assigned = @($user['assignedLicenses'] | Where-Object { $null -ne $_ } | ForEach-Object { [string] $_['skuId'] })
        $missing = @($wanted | Where-Object { [string] $_['skuId'] -notin $assigned })
        if ($missing.Count -eq 0) {
            return New-StepResult -Context $Context -Status done -Reason 'Lizenzen bereits zugewiesen.' -Output @{ skus = @($cloud['skus']) }
        }

        $empty = @($missing | Where-Object { ([int] $_['prepaidUnits']['enabled'] - [int] $_['consumedUnits']) -le 0 } | ForEach-Object { [string] $_['skuPartNumber'] })
        if ($empty.Count -gt 0) {
            return New-StepResult -Context $Context -Status failed -Code 'no-free-license' -Reason ('Keine freien Lizenzen für {0}.' -f ($empty -join ', '))
        }

        $add = @(foreach ($sku in $missing) {
                $disabled = @($sku['servicePlans'] | Where-Object { [string] $_['servicePlanName'] -in $disabledNames } | ForEach-Object { [string] $_['servicePlanId'] })
                @{ skuId = [string] $sku['skuId']; disabledPlans = $disabled }
            })
        $names = @($missing | ForEach-Object { [string] $_['skuPartNumber'] }) -join ', '
        $id = [string] $user['id']
        Invoke-StepChange -Context $Context -Description ('Lizenzen zuweisen: {0}' -f $names) -Action {
            Invoke-OnbGraph -Method POST -Uri ('v1.0/users/{0}/assignLicense' -f [uri]::EscapeDataString($id)) -Body @{ addLicenses = $add; removeLicenses = @() } | Out-Null
        } | Out-Null
        New-StepResult -Context $Context -Status done -Reason ('Zugewiesen: {0}.' -f $names) -Output @{ skus = @($cloud['skus']) }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StepMain -Handler ${function:Invoke-EntraAssignLicense}
}
