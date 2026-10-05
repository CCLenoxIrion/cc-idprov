using System.Collections.Frozen;
using Onboarding.Core.Domain;
using static Onboarding.Core.Domain.StepStatus;

namespace Onboarding.Core.State;

/// <summary>Allowed step status transitions (SPEC §5, §6).</summary>
public static class StepStateMachine
{
    private static readonly FrozenDictionary<StepStatus, FrozenSet<StepStatus>> Transitions =
        new Dictionary<StepStatus, FrozenSet<StepStatus>>
        {
            [Pending] = Set(Running, Skipped),
            [Running] = Set(Done, Waiting, Failed, Skipped, ManualTask, NeedsInput),
            [Waiting] = Set(Running, Failed),
            // Retry (-> Pending) or "mark as done" with reason (-> Done).
            [Failed] = Set(Pending, Done),
            // An ITAdmin performs the task and marks it done (DECISIONS S2).
            [ManualTask] = Set(Done),
            // Retry/force (-> Pending) or accept the existing state with reason (-> Done).
            [NeedsInput] = Set(Pending, Done),
            [Done] = Set(),
            [Skipped] = Set(),
        }.ToFrozenDictionary();

    public static bool CanTransition(StepStatus from, StepStatus to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureCanTransition(StepStatus from, StepStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidStateTransitionException($"Step status transition {from} -> {to} is not allowed.");
        }
    }

    public static IReadOnlySet<StepStatus> AllowedTargets(StepStatus from) => Transitions[from];

    /// <summary>A completed step satisfies dependencies of later steps.</summary>
    public static bool SatisfiesDependency(StepStatus status) => status is Done or Skipped;

    private static FrozenSet<StepStatus> Set(params StepStatus[] targets) => targets.ToFrozenSet();
}
