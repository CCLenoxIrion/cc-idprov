# Fixed configuration of the JEA endpoint "CC.Onboarding" on the file server DC01 (member server).
# Only administrators may change this file (it defines where the endpoint writes and which
# rights it may grant). Values must match the global configuration in the admin UI.
@{
    # Local root of the home folders (GlobalConfig.Home.LocalRoot).
    HomeRoot         = 'F:\Home'

    # Share name; {sam} is replaced (GlobalConfig.Home.ShareNamePattern).
    ShareNamePattern = '{sam}$'

    # Share permissions besides the user (Change). DECISIONS X18: deliberately stricter than the
    # existing shares (Everyone=Full). SMB cmdlets take account names, so the localized name of
    # the server's language is needed here (DC01 is German, verified 2026-10).
    ShareFullAccess  = @('VORDEFINIERT\Administratoren')

    # NetBIOS name of the domain (GlobalConfig.DomainNetBios).
    NetbiosDomain    = 'CC'

    # Owner of every home folder (inheritance is disabled): BUILTIN\Administrators as well-known SID,
    # because 'BUILTIN\Administrators' does not resolve on the German DC01 (verified 2026-10).
    HomeOwner        = 'S-1-5-32-544'

    # Allowlist (DECISIONS X17): rights the worker may request for the user (GlobalConfig.Home.UserRight).
    HomeUserRights   = @('Modify', 'FullControl')

    # Allowlist of additional ACEs: principal (exactly as in the admin UI, case-insensitive) →
    # allowed rights. Anything else is rejected with 'ace-not-allowed'. New principals in the
    # admin UI (global or per department) must be added here, too. SIDs are accepted as principals;
    # built-in principals are listed as SIDs (names are localized): S-1-5-18 = SYSTEM,
    # S-1-5-32-544 = BUILTIN\Administrators.
    HomeAceAllowlist = @{
        'S-1-5-18'              = @('FullControl')
        'S-1-5-32-544'          = @('FullControl')
        'CC\CC-Management-Lead' = @('FullControl')
    }
}
