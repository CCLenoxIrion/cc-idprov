using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Core.Time;
using static Onboarding.Core.Steps.StepKeys;

namespace Onboarding.Core.Steps;

/// <summary>Onboarding steps with dependencies and conditions (SPEC §7).</summary>
public sealed class OnboardingStepPlan : IStepPlanProvider
{
    private static readonly IReadOnlyList<StepDefinition> Definitions =
    [
        new(AdCreateUser, []),
        new(AdGroups, [AdCreateUser],
            c => c.Snapshot.Department.AdGroups.Count == 0 ? "Abteilung hat keine AD-Gruppen." : null),
        new(HomeFolder, [AdCreateUser]),
        new(HomeShare, [HomeFolder]),
        new(LogonScript, [HomeShare]),
        new(SyncDelta, [AdCreateUser, AdGroups, HomeFolder, HomeShare, LogonScript]),
        new(EntraWaitUser, [SyncDelta]),
        new(EntraUsageLocation, [EntraWaitUser]),
        new(EntraAssignLicense, [EntraUsageLocation],
            c => c.Snapshot.Global.LicenseMode == LicenseMode.Group
                ? "LicenseMode = Group: Lizenz kommt über die Lizenzgruppe (AD.Groups)."
                : null),
        new(EntraWaitLicense, [EntraAssignLicense]),
        new(ExoWaitMailbox, [EntraWaitLicense]),
        new(ExoDisableNewOutlook, [ExoWaitMailbox]),
        new(ExoSharedMailboxes, [ExoWaitMailbox],
            c => c.Snapshot.Department.SharedMailboxes.Count == 0 ? "Abteilung hat keine Freigabepostfächer." : null),
        new(TeamsWaitUser, [EntraWaitLicense], NoPhone),
        new(TeamsPhone, [TeamsWaitUser], NoPhone),
        new(TeamsVoiceRouting, [TeamsPhone], NoPhone),
        new(TeamsVoicemail, [TeamsPhone],
            c => NoPhone(c) ?? (c.Snapshot.Department.Teams.Voicemail ? null : "Voicemail für die Abteilung deaktiviert.")),
        new(TeamsForwarding, [TeamsPhone],
            c => NoPhone(c) ?? (c.Snapshot.Department.Teams.UnansweredForward.Enabled
                ? null
                : "Rufweiterleitung für die Abteilung deaktiviert.")),
        new(AdEnable, [AdCreateUser], NotBefore: c => EnableAt(c)),
    ];

    public RequestType Type => RequestType.Onboarding;

    public IReadOnlyList<StepDefinition> Steps => Definitions;

    /// <summary>Earliest time <c>AD.Enable</c> may run (DECISIONS S5).</summary>
    public static DateTimeOffset EnableAt(StepPlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var global = context.Snapshot.Global;
        return BusinessCalendar.StartOfDayUtc(context.Input.EffectiveDate, global.TimeZone, global.EnableLeadTime);
    }

    private static string? NoPhone(StepPlanContext c) =>
        c.HasExtension ? null : "Keine Durchwahl angegeben.";
}
