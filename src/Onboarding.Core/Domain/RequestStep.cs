using Onboarding.Core.State;

namespace Onboarding.Core.Domain;

/// <summary>One automated step of a request (SPEC §5, §7).</summary>
public sealed class RequestStep : IVersioned
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string StepKey { get; set; } = "";

    /// <summary>Position in the step plan, for display.</summary>
    public int SortOrder { get; set; }

    public StepStatus Status { get; private set; } = StepStatus.Pending;
    public int Attempts { get; internal set; }

    /// <summary>Earliest time the worker may (re)try the step. Null = immediately.</summary>
    public DateTimeOffset? NextAttemptAt { get; internal set; }

    /// <summary>Start of the first attempt; the step timeout is measured from here.</summary>
    public DateTimeOffset? FirstAttemptAt { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    /// <summary>Last error message. Must never contain secrets.</summary>
    public string? LastError { get; internal set; }

    /// <summary>Step output (JSON). Must never contain secrets.</summary>
    public string? OutputJson { get; internal set; }

    /// <summary>Skip reason, manual-task instructions or the reason for "mark as done".</summary>
    public string? Note { get; internal set; }

    public long Version { get; set; }

    internal void TransitionTo(StepStatus target, DateTimeOffset now)
    {
        StepStateMachine.EnsureCanTransition(Status, target);
        Status = target;
        if (target is StepStatus.Done or StepStatus.Skipped)
        {
            CompletedAt = now;
        }
    }

    /// <summary>Initial status assigned by the step plan (Pending or Skipped).</summary>
    internal void InitializeAs(StepStatus initial)
    {
        if (initial is not (StepStatus.Pending or StepStatus.Skipped))
        {
            throw new ArgumentOutOfRangeException(nameof(initial), initial, "Steps start as Pending or Skipped.");
        }

        Status = initial;
    }
}
