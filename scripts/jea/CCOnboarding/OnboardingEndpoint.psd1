# Fixed configuration of the JEA endpoint "CC.Onboarding" on the home server / DC01.
# Only administrators may change this file (it defines where the endpoint writes).
# Values must match the global configuration in the admin UI (Home, LogonScript).
@{
    # Local root of the home folders (GlobalConfig.Home.LocalRoot).
    HomeRoot             = 'F:\Home'

    # Share name; {sam} is replaced (GlobalConfig.Home.ShareNamePattern).
    ShareNamePattern     = '{sam}$'

    # Accounts with Full Control on each home share besides the user (Change).
    # ZU VERIFIZIEREN an einer bestehenden Home-Freigabe (Get-SmbShareAccess), SPEC §11.
    ShareFullAccess      = @('BUILTIN\Administrators')

    # NetBIOS name of the domain (GlobalConfig.DomainNetBios, SPEC §11 noch offen).
    NetbiosDomain        = 'CC'

    # Local folder behind the NETLOGON share on this DC (replicated by DFSR).
    # ZU VERIFIZIEREN: (Get-SmbShare NETLOGON).Path
    LogonScriptDirectory = 'C:\Windows\SYSVOL\sysvol\CC.local\scripts'

    # File name; {sam} is replaced (GlobalConfig.LogonScript.FileNamePattern).
    LogonFileNamePattern = '{sam}.bat'

    # Upper bound for logon script content in bytes.
    MaxLogonScriptBytes  = 65536
}
