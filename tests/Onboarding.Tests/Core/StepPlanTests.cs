using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Core.Steps;
using Onboarding.Tests.TestSupport;
using static Onboarding.Core.Steps.StepKeys;

namespace Onboarding.Tests.Core;

public sealed class StepPlanTests
{
    private readonly RequestFactory _f = new();

    private static string[] Skipped(Request r) =>
        r.Steps.Where(s => s.Status == StepStatus.Skipped).Select(s => s.StepKey).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Plan_covers_all_spec_section_7_keys_in_dependency_order()
    {
        var plan = new OnboardingStepPlan().Steps;
        var keys = typeof(StepKeys).GetFields().Select(f => (string)f.GetValue(null)!).ToHashSet();

        Assert.Equal(keys, plan.Select(s => s.Key).ToHashSet());
        Assert.Equal(19, plan.Count);

        var seen = new HashSet<string>();
        foreach (var step in plan)
        {
            Assert.All(step.DependsOn, d => Assert.Contains(d, seen));
            seen.Add(step.Key);
        }
    }

    [Fact]
    public void Offboarding_is_not_supported_in_v1()
    {
        Assert.False(StepPlanRegistry.Default.Supports(RequestType.Offboarding));
        Assert.Throws<NotSupportedException>(() => StepPlanRegistry.Default.For(RequestType.Offboarding));
    }

    [Fact]
    public void Without_extension_all_teams_steps_are_skipped()
    {
        var request = _f.Approved(TestConfig.Person(extension: null));

        Assert.Equal(
            new[] { TeamsForwarding, TeamsPhone, TeamsVoiceRouting, TeamsVoicemail, TeamsWaitUser }.Order(StringComparer.Ordinal),
            Skipped(request));
        Assert.All(request.Steps.Where(s => s.Status == StepStatus.Skipped), s => Assert.False(string.IsNullOrEmpty(s.Note)));
    }

    [Fact]
    public void With_extension_teams_steps_are_planned()
    {
        var request = _f.Approved(TestConfig.Person(extension: "12"));

        Assert.Empty(Skipped(request));
        Assert.Equal(19, request.Steps.Count);
    }

    [Fact]
    public void Department_without_voicemail_and_forwarding_skips_those()
    {
        var snapshot = TestConfig.Snapshot(department: d =>
        {
            d.Teams.Voicemail = false;
            d.Teams.UnansweredForward.Enabled = false;
        });

        var request = _f.Approved(TestConfig.Person(extension: "12"), snapshot);

        Assert.Equal([TeamsForwarding, TeamsVoicemail], Skipped(request));
    }

    [Fact]
    public void Group_license_mode_skips_assign_license()
    {
        var request = _f.Approved(snapshot: TestConfig.Snapshot(global: g => g.LicenseMode = LicenseMode.Group));

        Assert.Contains(EntraAssignLicense, Skipped(request));
        Assert.Equal(StepStatus.Pending, request.FindStep(EntraWaitLicense)!.Status);
    }

    [Fact]
    public void Empty_groups_and_mailboxes_are_skipped()
    {
        var snapshot = TestConfig.Snapshot(department: d =>
        {
            d.AdGroups.Clear();
            d.SharedMailboxes.Clear();
        });

        var request = _f.Approved(snapshot: snapshot);

        Assert.Contains(AdGroups, Skipped(request));
        Assert.Contains(ExoSharedMailboxes, Skipped(request));
    }

    [Fact]
    public void Ad_enable_is_not_before_midnight_berlin_of_entry_date()
    {
        var request = _f.Approved(TestConfig.Person()); // entry 2026-11-02 (CET)

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero), request.FindStep(AdEnable)!.NextAttemptAt);
        Assert.Null(request.FindStep(AdCreateUser)!.NextAttemptAt);
    }

    [Fact]
    public void Ad_enable_respects_lead_time()
    {
        var request = _f.Approved(snapshot: TestConfig.Snapshot(global: g => g.EnableLeadTime = TimeSpan.FromHours(12)));

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), request.FindStep(AdEnable)!.NextAttemptAt);
    }
}
