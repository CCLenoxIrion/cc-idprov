# Fixed configuration of the JEA endpoint "CC.Onboarding.Logon" on the domain controller DC03.
# Only administrators may change this file. Values must match the global configuration
# (GlobalConfig.LogonScript.Server / FileNamePattern).
@{
    # Local folder behind NETLOGON on DC03 (verified; DFSR state "Eliminated").
    LogonScriptDirectory = 'C:\Windows\SYSVOL\sysvol\CC.local\SCRIPTS'

    # File name; {sam} is replaced (GlobalConfig.LogonScript.FileNamePattern).
    LogonFileNamePattern = '{sam}.bat'

    # Upper bound for logon script content in bytes.
    MaxLogonScriptBytes  = 65536
}
