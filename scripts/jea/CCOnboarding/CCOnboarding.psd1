@{
    RootModule        = 'CCOnboarding.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = '5d0f8c3e-2a51-4c8e-9a3b-7b1c2f6e4a01'
    Author            = 'CleanControlling IT'
    Description       = 'JEA functions for onboarding on the home server / DC01 (home folder, home share, logon script).'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('New-OnbHomeFolder', 'New-OnbHomeShare', 'Set-OnbLogonScript')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
