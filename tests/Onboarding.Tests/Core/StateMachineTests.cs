using Onboarding.Core.Domain;
using Onboarding.Core.State;
using static Onboarding.Core.Domain.RequestStatus;

namespace Onboarding.Tests.Core;

public sealed class StateMachineTests
{
    private static readonly HashSet<(RequestStatus, RequestStatus)> AllowedRequest =
    [
        (Draft, PendingApproval), (Draft, Cancelled),
        (PendingApproval, Approved), (PendingApproval, NeedsInput), (PendingApproval, Cancelled),
        (Approved, Running), (Approved, NeedsInput), (Approved, Cancelled),
        (Running, Waiting), (Running, AwaitingChecklist), (Running, Failed), (Running, NeedsInput), (Running, Cancelled),
        (Waiting, Running), (Waiting, AwaitingChecklist), (Waiting, Failed), (Waiting, NeedsInput), (Waiting, Cancelled),
        (AwaitingChecklist, Completed), (AwaitingChecklist, Running), (AwaitingChecklist, Waiting),
        (AwaitingChecklist, Failed), (AwaitingChecklist, Cancelled),
        (Failed, Running), (Failed, Waiting), (Failed, AwaitingChecklist), (Failed, Cancelled),
        (NeedsInput, PendingApproval), (NeedsInput, Approved), (NeedsInput, Cancelled),
    ];

    private static readonly HashSet<(StepStatus, StepStatus)> AllowedStep =
    [
        (StepStatus.Pending, StepStatus.Running), (StepStatus.Pending, StepStatus.Skipped),
        (StepStatus.Running, StepStatus.Done), (StepStatus.Running, StepStatus.Waiting), (StepStatus.Running, StepStatus.Failed),
        (StepStatus.Running, StepStatus.Skipped), (StepStatus.Running, StepStatus.ManualTask),
        (StepStatus.Waiting, StepStatus.Running), (StepStatus.Waiting, StepStatus.Failed),
        (StepStatus.Failed, StepStatus.Pending), (StepStatus.Failed, StepStatus.Done),
        (StepStatus.ManualTask, StepStatus.Done),
    ];

    public static TheoryData<RequestStatus, RequestStatus> AllRequestPairs()
    {
        var data = new TheoryData<RequestStatus, RequestStatus>();
        foreach (var from in Enum.GetValues<RequestStatus>())
        {
            foreach (var to in Enum.GetValues<RequestStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    public static TheoryData<StepStatus, StepStatus> AllStepPairs()
    {
        var data = new TheoryData<StepStatus, StepStatus>();
        foreach (var from in Enum.GetValues<StepStatus>())
        {
            foreach (var to in Enum.GetValues<StepStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllRequestPairs))]
    public void Request_transitions_match_table(RequestStatus from, RequestStatus to)
    {
        var allowed = AllowedRequest.Contains((from, to));

        Assert.Equal(allowed, RequestStateMachine.CanTransition(from, to));
        if (!allowed)
        {
            Assert.Throws<InvalidStateTransitionException>(() => RequestStateMachine.EnsureCanTransition(from, to));
        }
    }

    [Theory]
    [MemberData(nameof(AllStepPairs))]
    public void Step_transitions_match_table(StepStatus from, StepStatus to)
    {
        Assert.Equal(AllowedStep.Contains((from, to)), StepStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void Terminal_request_states_have_no_exits()
    {
        Assert.Empty(RequestStateMachine.AllowedTargets(Completed));
        Assert.Empty(RequestStateMachine.AllowedTargets(Cancelled));
        Assert.True(RequestStateMachine.IsTerminal(Completed));
        Assert.True(RequestStateMachine.IsTerminal(Cancelled));
        Assert.False(RequestStateMachine.IsTerminal(Failed));
    }

    [Fact]
    public void Every_status_has_a_transition_entry()
    {
        foreach (var status in Enum.GetValues<RequestStatus>())
        {
            _ = RequestStateMachine.AllowedTargets(status);
        }

        foreach (var status in Enum.GetValues<StepStatus>())
        {
            _ = StepStateMachine.AllowedTargets(status);
        }
    }
}
