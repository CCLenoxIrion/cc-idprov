@{
    RootModule        = 'CCOnboardingLogon.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = 'b2f4a9d1-7c3e-4e58-9a61-0d2c8e4f7a15'
    Author            = 'CleanControlling IT'
    Description       = 'JEA function for onboarding on the domain controller DC03 (logon script in NETLOGON).'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('Set-OnbLogonScript')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
