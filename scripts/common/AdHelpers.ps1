<#
.SYNOPSIS
    AD helpers shared by the step scripts. Dot-sourced by the scripts, so AD cmdlet calls
    originate in script scope (mockable in Pester without -ModuleName).

.NOTES
    AD queries only via -Identity or -LDAPFilter with escaped values; never string
    interpolation into -Filter (DECISIONS X9).
#>

function Import-ActiveDirectoryModule {
    if (-not (Get-Command -Name Get-ADUser -ErrorAction SilentlyContinue)) {
        Import-Module ActiveDirectory -ErrorAction Stop
    }
}

function Find-AdUserBySam {
    param(
        [Parameter(Mandatory)] [string] $Sam,
        [string[]] $Properties = @()
    )

    Assert-SamAccountName $Sam
    $filter = '(&(objectCategory=person)(objectClass=user)(sAMAccountName={0}))' -f (ConvertTo-LdapFilterValue $Sam)
    return Get-ADUser -LDAPFilter $filter -Properties $Properties -ErrorAction Stop | Select-Object -First 1
}

function Test-OwnAdAccount {
    <#
    .SYNOPSIS
        An account belongs to the request if its objectGUID is the stored one or the
        request-id attribute carries the request id (DECISIONS K4/K6).
    #>
    param(
        [Parameter(Mandatory)] $User,
        [Parameter(Mandatory)] [hashtable] $In
    )

    if ($In['directoryObjectGuid'] -and [string] $User.ObjectGUID -eq [string] $In['directoryObjectGuid']) {
        return $true
    }

    $attribute = [string] $In['config']['requestIdAttribute']
    if ($attribute -and $User.PSObject.Properties.Name -contains $attribute) {
        return [string] $User.$attribute -eq [string] $In['requestId']
    }

    return $false
}

function Get-OwnAdAccount {
    <# Returns the request's own account, or a step result (needsInput/failed) if there is none. #>
    param(
        [Parameter(Mandatory)] [hashtable] $In,
        [Parameter(Mandatory)] $Context,
        [string[]] $Properties = @()
    )

    $attribute = [string] $In['config']['requestIdAttribute']
    $props = @($Properties) + @($attribute) | Where-Object { $_ } | Select-Object -Unique
    $user = Find-AdUserBySam -Sam $In['identity']['sam'] -Properties $props
    if ($null -eq $user) {
        return @{ Result = (New-StepResult -Context $Context -Status failed -Reason 'Konto des Auftrags nicht gefunden.') }
    }

    if (-not (Test-OwnAdAccount -User $user -In $In)) {
        return @{ Result = (New-StepResult -Context $Context -Status needsInput -Reason 'Das Konto mit diesem sAMAccountName gehört nicht zu diesem Auftrag.') }
    }

    return @{ User = $user }
}

function Get-ParentDn {
    param([Parameter(Mandatory)] [string] $DistinguishedName)

    # Split at the first comma that is not escaped.
    $parts = [regex]::Split($DistinguishedName, '(?<!\\),', 2)
    if ($parts.Count -lt 2) { return '' }
    return $parts[1]
}
