using System.Collections.Frozen;
using Onboarding.Core.Domain;
using static Onboarding.Core.Domain.RequestStatus;

namespace Onboarding.Core.State;

/// <summary>
/// Allowed request status transitions (SPEC §5, DECISIONS S1). Type-independent so that
/// offboarding (v2) uses the same lifecycle.
/// </summary>
public static class RequestStateMachine
{
    private static readonly FrozenDictionary<RequestStatus, FrozenSet<RequestStatus>> Transitions =
        new Dictionary<RequestStatus, FrozenSet<RequestStatus>>
        {
            [Draft] = Set(PendingApproval, Cancelled),
            [PendingApproval] = Set(Approved, NeedsInput, Cancelled),
            [Approved] = Set(Running, NeedsInput, Cancelled),
            [Running] = Set(Waiting, AwaitingChecklist, Failed, NeedsInput, Cancelled),
            [Waiting] = Set(Running, AwaitingChecklist, Failed, NeedsInput, Cancelled),
            [AwaitingChecklist] = Set(Completed, Running, Waiting, Failed, NeedsInput, Cancelled),
            // Retry / "mark as done" brings a failed request back to Running.
            [Failed] = Set(Running, Waiting, AwaitingChecklist, NeedsInput, Cancelled),
            // Identity: back to PendingApproval if never approved (keeps four-eyes), else Approved.
            // Step in NeedsInput resolved by an admin action: back into execution.
            [NeedsInput] = Set(PendingApproval, Approved, Running, Waiting, AwaitingChecklist, Failed, Cancelled),
            [Completed] = Set(),
            [Cancelled] = Set(),
        }.ToFrozenDictionary();

    public static bool CanTransition(RequestStatus from, RequestStatus to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureCanTransition(RequestStatus from, RequestStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException($"Request status transition {from} -> {to} is not allowed.");
        }
    }

    public static IReadOnlySet<RequestStatus> AllowedTargets(RequestStatus from) => Transitions[from];

    public static bool IsTerminal(RequestStatus status) => status is Completed or Cancelled;

    /// <summary>Statuses in which the request's steps are being executed.</summary>
    public static bool IsExecuting(RequestStatus status) =>
        status is Approved or Running or Waiting or AwaitingChecklist;

    /// <summary>
    /// Statuses in which the worker looks for due steps. Failed/NeedsInput are included: such a
    /// step only blocks its dependents, independent steps continue (SPEC §6).
    /// </summary>
    public static bool IsProcessable(RequestStatus status) =>
        IsExecuting(status) || status is Failed or NeedsInput;

    private static FrozenSet<RequestStatus> Set(params RequestStatus[] targets) => targets.ToFrozenSet();
}
