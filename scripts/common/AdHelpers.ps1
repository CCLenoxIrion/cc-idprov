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
        return @{ Result = (New-StepResult -Context $Context -Status failed -Code 'account-not-found' -Reason 'Konto des Auftrags nicht gefunden.') }
    }

    if (-not (Test-OwnAdAccount -User $user -In $In)) {
        return @{ Result = (New-StepResult -Context $Context -Status needsInput -Code 'foreign-account' -Reason 'Das Konto mit diesem sAMAccountName gehört nicht zu diesem Auftrag.') }
    }

    return @{ User = $user }
}

function Split-DnFirstRdn {
    <#
    .SYNOPSIS
        Splits a DN at the first unescaped comma: @(<first RDN>, <rest>). Escapes (RFC 4514:
        backslash + character, e.g. "\," or "\\") are skipped, so "CN=Irion\, Lenox,OU=X"
        yields "CN=Irion\, Lenox" and "OU=X".
    .NOTES
        Deliberately no [regex]::Split($dn, $pattern, 2): the static overload's third argument
        is RegexOptions, not a count – that bug split at every comma.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $DistinguishedName)

    for ($i = 0; $i -lt $DistinguishedName.Length; $i++) {
        $char = $DistinguishedName[$i]
        if ($char -eq '\') {
            $i++
            continue
        }
        if ($char -eq ',') {
            return @($DistinguishedName.Substring(0, $i), $DistinguishedName.Substring($i + 1))
        }
    }

    return @($DistinguishedName, '')
}

function Get-ParentDn {
    <# DN without its first RDN; '' if there is no parent. #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $DistinguishedName)

    return (Split-DnFirstRdn $DistinguishedName)[1]
}

function ConvertTo-NormalizedDn {
    <# RDNs trimmed (spaces around unescaped commas and '='), lower case – for comparisons only. #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $DistinguishedName)

    $rdns = [System.Collections.Generic.List[string]]::new()
    $rest = $DistinguishedName
    while ($rest.Length -gt 0) {
        $parts = Split-DnFirstRdn $rest
        $rdn = $parts[0].Trim()
        $eq = $rdn.IndexOf('=')
        if ($eq -gt 0) {
            $rdn = $rdn.Substring(0, $eq).Trim() + '=' + $rdn.Substring($eq + 1).Trim()
        }
        $rdns.Add($rdn.ToLowerInvariant())
        $rest = $parts[1]
    }

    return ($rdns -join ',')
}

function Test-SameDn {
    <# DN equality as AD treats it: case-insensitive, spaces around separators ignored. #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Left,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Right
    )

    return (ConvertTo-NormalizedDn $Left) -ceq (ConvertTo-NormalizedDn $Right)
}
