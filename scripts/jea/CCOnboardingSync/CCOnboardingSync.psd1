@{
    RootModule        = 'CCOnboardingSync.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = '3c9b5e17-6d2a-4f80-b4c1-9e8a7d6f5b03'
    Author            = 'CleanControlling IT'
    Description       = 'JEA function for onboarding on the Entra Connect server (delta sync).'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('Start-OnbDeltaSync')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
