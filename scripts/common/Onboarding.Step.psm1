#Requires -Version 7.2
<#
.SYNOPSIS
    Shared helpers for the onboarding step scripts (DECISIONS X3).

.DESCRIPTION
    Contract:
      * Input only as JSON on stdin (never command-line parameters, so secrets do not appear
        in process lists or logs).
      * Output only as one JSON object on stdout:
        { status, reason, output, directoryObjectGuid, dryRun, plannedActions }.
      * Errors are always structured. Messages are sanitized: never raw exception messages,
        never parameter values, never secrets.
      * Dry-run: every change goes through Invoke-StepChange, which only records the planned
        action when dryRun is true.
#>

Set-StrictMode -Version Latest

$script:SafeMarker = 'OnbSafeMessage'

function New-StepContext {
    [CmdletBinding()]
    param([bool] $DryRun)

    [pscustomobject]@{
        DryRun         = $DryRun
        PlannedActions = [System.Collections.Generic.List[string]]::new()
    }
}

function Read-StepInput {
    <# Reads and parses the JSON input; -Json is for tests only. #>
    [CmdletBinding()]
    param([string] $Json)

    if ([string]::IsNullOrWhiteSpace($Json)) {
        $Json = [Console]::In.ReadToEnd()
    }

    if ([string]::IsNullOrWhiteSpace($Json)) {
        throw (New-SafeException 'Keine Eingabe auf stdin erhalten.')
    }

    try {
        return $Json | ConvertFrom-Json -AsHashtable -Depth 20 -ErrorAction Stop
    }
    catch {
        throw (New-SafeException 'Eingabe ist kein gültiges JSON.')
    }
}

function New-SafeException {
    <# An exception whose message is known to be free of secrets and may be returned as reason. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Message)

    $exception = [System.InvalidOperationException]::new($Message)
    $exception.Data[$script:SafeMarker] = $true
    return $exception
}

function Get-SafeErrorReason {
    <#
    .SYNOPSIS
        Maps an error to a fixed, sanitized text. Raw exception messages are never returned
        (they may contain parameter values), only messages created via New-SafeException.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $ErrorRecord)

    $exception = if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $ErrorRecord.Exception } else { $ErrorRecord }
    while ($exception -is [System.Management.Automation.RuntimeException] -and $null -ne $exception.InnerException -and
        -not $exception.Data.Contains($script:SafeMarker)) {
        $exception = $exception.InnerException
    }

    if ($exception.Data.Contains($script:SafeMarker)) {
        return $exception.Message
    }

    switch ($exception.GetType().FullName) {
        'Microsoft.ActiveDirectory.Management.ADIdentityNotFoundException' { return 'AD-Objekt nicht gefunden.' }
        'Microsoft.ActiveDirectory.Management.ADIdentityAlreadyExistsException' { return 'AD-Objekt existiert bereits.' }
        'Microsoft.ActiveDirectory.Management.ADPasswordComplexityException' { return 'Startpasswort entspricht nicht der Domänen-Kennwortrichtlinie.' }
        'Microsoft.ActiveDirectory.Management.ADServerDownException' { return 'Domänencontroller nicht erreichbar.' }
        'System.UnauthorizedAccessException' { return 'Zugriff verweigert.' }
        'System.Management.Automation.Remoting.PSRemotingTransportException' { return 'JEA-Endpunkt nicht erreichbar oder Zugriff verweigert.' }
        'System.Management.Automation.CommandNotFoundException' { return 'Benötigter Befehl nicht verfügbar (Modul installiert?).' }
    }

    if ($exception.GetType().FullName -like 'Microsoft.ActiveDirectory.Management.*') {
        return 'AD-Operation fehlgeschlagen ({0}).' -f $exception.GetType().Name
    }

    return 'Unerwarteter Fehler ({0}).' -f $exception.GetType().Name
}

function Invoke-StepChange {
    <#
    .SYNOPSIS
        Executes a change, or only records it in dry-run mode. Returns the action's output.
    .PARAMETER Description
        Human-readable description without secrets (shown as planned action).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Description,
        [Parameter(Mandatory)] [scriptblock] $Action
    )

    $Context.PlannedActions.Add($Description)
    if ($Context.DryRun) {
        return $null
    }

    return & $Action
}

function New-StepResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [ValidateSet('done', 'waiting', 'failed', 'needsInput', 'manualTask', 'skipped')] [string] $Status,
        [string] $Reason,
        [hashtable] $Output,
        [string] $DirectoryObjectGuid
    )

    $result = [ordered]@{
        status         = $Status
        reason         = $Reason
        output         = $Output
        dryRun         = [bool] $Context.DryRun
        plannedActions = @($Context.PlannedActions)
    }
    if ($DirectoryObjectGuid) {
        $result.directoryObjectGuid = $DirectoryObjectGuid
    }

    return $result
}

function ConvertTo-StepJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Result)

    return ($Result | ConvertTo-Json -Depth 10 -Compress)
}

function Invoke-StepMain {
    <#
    .SYNOPSIS
        Entry point of every step script: read input, run the handler, write exactly one JSON
        object. Any error becomes a sanitized 'failed' result.
    .PARAMETER Handler
        Scriptblock taking (input hashtable, context).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [scriptblock] $Handler,
        [string] $Json
    )

    $context = New-StepContext -DryRun $false
    try {
        $stepInput = Read-StepInput -Json $Json
        $context = New-StepContext -DryRun ([bool] $stepInput['dryRun'])
        $result = & $Handler $stepInput $context
    }
    catch {
        $result = New-StepResult -Context $context -Status failed -Reason (Get-SafeErrorReason $_)
    }

    $json = ConvertTo-StepJson $result
    [Console]::Out.WriteLine($json)
    return $null
}

function ConvertTo-LdapFilterValue {
    <# RFC 4515 escaping for values in LDAP filters (DECISIONS X9). #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

    $builder = [System.Text.StringBuilder]::new($Value.Length + 8)
    foreach ($char in $Value.ToCharArray()) {
        $code = [int] $char
        if ($code -eq 0x5C) { [void] $builder.Append('\5c') }
        elseif ($code -eq 0x2A) { [void] $builder.Append('\2a') }
        elseif ($code -eq 0x28) { [void] $builder.Append('\28') }
        elseif ($code -eq 0x29) { [void] $builder.Append('\29') }
        elseif ($code -eq 0x00) { [void] $builder.Append('\00') }
        else { [void] $builder.Append($char) }
    }

    return $builder.ToString()
}

function Assert-SamAccountName {
    [CmdletBinding()]
    param([AllowEmptyString()] [AllowNull()] [string] $Sam)

    if ($null -eq $Sam -or $Sam -cnotmatch '^[a-z0-9]{1,20}$') {
        throw (New-SafeException 'Ungültiger sAMAccountName in der Eingabe.')
    }
}

function Invoke-JeaFunction {
    <#
    .SYNOPSIS
        Calls one function of a JEA endpoint via implicit remoting (DECISIONS X5).
    .DESCRIPTION
        JEA sessions run in NoLanguage mode: script blocks with variables ($using:, param) are
        not allowed there. The function is therefore imported as a local proxy
        (Import-PSSession with prefix "Remote") and called locally with parameters. The session
        is always closed. ZU VERIFIZIEREN gegen den registrierten Endpunkt (DEPLOYMENT.md).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ComputerName,
        [Parameter(Mandatory)] [string] $ConfigurationName,
        [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z]+-Onb[A-Za-z]+$')] [string] $FunctionName,
        [hashtable] $Parameters = @{}
    )

    $session = $null
    $module = $null
    try {
        $session = New-PSSession -ComputerName $ComputerName -ConfigurationName $ConfigurationName -ErrorAction Stop
        $module = Import-PSSession -Session $session -CommandName $FunctionName -Prefix Remote -AllowClobber -DisableNameChecking -ErrorAction Stop
        $verb, $noun = $FunctionName -split '-', 2
        $proxy = '{0}-Remote{1}' -f $verb, $noun
        return & $proxy @Parameters
    }
    finally {
        if ($null -ne $module) { Remove-Module -ModuleInfo $module -Force -ErrorAction SilentlyContinue }
        if ($null -ne $session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue }
    }
}

function ConvertFrom-JeaResult {
    <#
    .SYNOPSIS
        Turns the (deserialized) result object of a JEA function into a step result. Planned
        actions of a dry-run are merged into the context.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Context,
        [AllowNull()] $JeaResult
    )

    if ($null -eq $JeaResult -or -not ($JeaResult.PSObject.Properties.Name -contains 'status')) {
        return New-StepResult -Context $Context -Status failed -Reason 'JEA-Endpunkt lieferte kein gültiges Ergebnis.'
    }

    if ($JeaResult.PSObject.Properties.Name -contains 'plannedActions') {
        foreach ($action in @($JeaResult.plannedActions)) {
            if ($action) { $Context.PlannedActions.Add([string] $action) }
        }
    }

    $output = @{}
    if ($JeaResult.PSObject.Properties.Name -contains 'output' -and $null -ne $JeaResult.output) {
        foreach ($property in $JeaResult.output.PSObject.Properties) {
            if ($property.MemberType -in 'NoteProperty', 'Property') { $output[$property.Name] = $property.Value }
        }
        if ($JeaResult.output -is [hashtable]) {
            foreach ($key in $JeaResult.output.Keys) { $output[$key] = $JeaResult.output[$key] }
        }
    }

    $status = [string] $JeaResult.status
    if ($status -notin 'done', 'waiting', 'failed', 'needsInput', 'manualTask', 'skipped') {
        return New-StepResult -Context $Context -Status failed -Reason 'JEA-Endpunkt lieferte einen unbekannten Status.'
    }

    $reason = if ($JeaResult.PSObject.Properties.Name -contains 'reason') { [string] $JeaResult.reason } else { $null }
    return New-StepResult -Context $Context -Status $status -Reason $reason -Output $output
}

Export-ModuleMember -Function New-StepContext, Read-StepInput, New-SafeException, Get-SafeErrorReason,
    Invoke-StepChange, New-StepResult, ConvertTo-StepJson, Invoke-StepMain, ConvertTo-LdapFilterValue,
    Assert-SamAccountName, Invoke-JeaFunction, ConvertFrom-JeaResult
