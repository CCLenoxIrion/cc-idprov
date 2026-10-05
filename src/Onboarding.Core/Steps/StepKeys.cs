namespace Onboarding.Core.Steps;

/// <summary>Onboarding step keys (SPEC §7). Offboarding keys (§8) are added in v2.</summary>
public static class StepKeys
{
    public const string AdCreateUser = "AD.CreateUser";
    public const string AdGroups = "AD.Groups";
    public const string HomeFolder = "Home.Folder";
    public const string HomeShare = "Home.Share";
    public const string LogonScript = "Logon.Script";
    public const string SyncDelta = "Sync.Delta";
    public const string EntraWaitUser = "Entra.WaitUser";
    public const string EntraUsageLocation = "Entra.UsageLocation";
    public const string EntraAssignLicense = "Entra.AssignLicense";
    public const string EntraWaitLicense = "Entra.WaitLicense";
    public const string ExoWaitMailbox = "EXO.WaitMailbox";
    public const string ExoDisableNewOutlook = "EXO.DisableNewOutlook";
    public const string ExoSharedMailboxes = "EXO.SharedMailboxes";
    public const string TeamsWaitUser = "Teams.WaitUser";
    public const string TeamsPhone = "Teams.Phone";
    public const string TeamsVoiceRouting = "Teams.VoiceRouting";
    public const string TeamsVoicemail = "Teams.Voicemail";
    public const string TeamsForwarding = "Teams.Forwarding";
    public const string AdEnable = "AD.Enable";
}
