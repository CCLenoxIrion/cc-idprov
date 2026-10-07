using Microsoft.Extensions.Time.Testing;
using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Core.Naming;
using Onboarding.Core.Steps;
using Onboarding.Core.Workflow;

namespace Onboarding.Tests.TestSupport;

/// <summary>Builds requests in a given lifecycle state for tests.</summary>
internal sealed class RequestFactory
{
    public static readonly Actor Hr = Actor.Create("hr.user", Role.Requester);
    public static readonly Actor Admin = Actor.Create("it.admin", Role.ITAdmin);
    public static readonly Actor Admin2 = Actor.Create("it.admin2", Role.ITAdmin);

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
    public RequestWorkflow Workflow { get; }

    public RequestFactory()
    {
        Workflow = new RequestWorkflow(Time);
    }

    public static IdentityCheck Check(PersonInput input, IdentityOverride? identityOverride = null) =>
        IdentityCheck.From(
            IdentityDeriver.Derive(input, identityOverride, TestConfig.Global(), TestConfig.Area(), TestConfig.Department()),
            []);

    public static IReadOnlyList<ChecklistTemplate> Templates() =>
    [
        new() { Id = Guid.NewGuid(), Title = "Pflicht 1", Mandatory = true, SortOrder = 1 },
        new() { Id = Guid.NewGuid(), Title = "Pflicht 2", Mandatory = true, SortOrder = 2 },
        new() { Id = Guid.NewGuid(), Title = "Optional", Mandatory = false, SortOrder = 3 },
    ];

    public Request Draft(PersonInput? input = null) =>
        Workflow.Create(RequestType.Onboarding, input ?? TestConfig.Person(), Hr, Templates()).Request;

    public Request Submitted(PersonInput? input = null)
    {
        var request = Draft(input);
        Workflow.Submit(request, Hr, Check(request.Input));
        return request;
    }

    public Request Approved(PersonInput? input = null, RequestConfigSnapshot? snapshot = null)
    {
        var request = Submitted(input);
        Workflow.Approve(request, Admin, snapshot ?? TestConfig.Snapshot(), Check(request.Input), [1, 2, 3]);
        return request;
    }

    /// <summary>Simulates the worker: Pending → Running → Done.</summary>
    public void CompleteStep(Request request, string key)
    {
        var step = request.FindStep(key)!;
        if (step.Status == StepStatus.Pending)
        {
            step.TransitionTo(StepStatus.Running, Time.GetUtcNow());
        }

        step.TransitionTo(StepStatus.Done, Time.GetUtcNow());
    }

    public void FailStep(Request request, string key)
    {
        var step = request.FindStep(key)!;
        step.TransitionTo(StepStatus.Running, Time.GetUtcNow());
        step.TransitionTo(StepStatus.Failed, Time.GetUtcNow());
    }

    public void CompleteAllSteps(Request request)
    {
        foreach (var step in request.Steps.Where(s => s.Status == StepStatus.Pending).ToList())
        {
            CompleteStep(request, step.StepKey);
        }
    }

    public static string[] PendingKeys(Request request) =>
        request.Steps.Where(s => s.Status == StepStatus.Pending).Select(s => s.StepKey).ToArray();
}
