using Onboarding.Core.Configuration;
using Onboarding.Core.State;

namespace Onboarding.Core.Domain;

/// <summary>
/// An onboarding (v1) or offboarding (v2) request. Status changes go through
/// <see cref="RequestStateMachine"/>; use <see cref="Workflow.RequestWorkflow"/> for actions.
/// </summary>
public sealed class Request : IVersioned
{
    public Guid Id { get; set; }
    public RequestType Type { get; set; }
    public RequestStatus Status { get; private set; } = RequestStatus.Draft;
    public PersonInput Input { get; set; } = new();

    /// <summary>Manually assigned sam/mail (ITAdmin), if any.</summary>
    public IdentityOverride? IdentityOverride { get; internal set; }

    /// <summary>Derived values; null until derivation succeeded.</summary>
    public DerivedIdentity? Derived { get; internal set; }

    /// <summary>Why the request is in <see cref="RequestStatus.NeedsInput"/>, for display.</summary>
    public string? NeedsInputReason { get; internal set; }

    /// <summary>Configuration frozen at approval (DECISIONS S3).</summary>
    public RequestConfigSnapshot? ConfigSnapshot { get; internal set; }

    /// <summary>
    /// Initial password, encrypted for the worker certificate (DECISIONS P1). Cleared once
    /// <c>AD.CreateUser</c> is done. Never contains plaintext.
    /// </summary>
    public byte[]? EncryptedInitialPassword { get; internal set; }

    public string CreatedBy { get; set; } = "";
    public string? ApprovedBy { get; internal set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; internal set; }
    public DateTimeOffset? ApprovedAt { get; internal set; }
    public DateTimeOffset? ClosedAt { get; internal set; }

    public List<RequestStep> Steps { get; private set; } = [];
    public List<ChecklistItem> Checklist { get; private set; } = [];

    public long Version { get; set; }

    public bool IsClosed => RequestStateMachine.IsTerminal(Status);

    public RequestStep? FindStep(string stepKey) =>
        Steps.FirstOrDefault(s => string.Equals(s.StepKey, stepKey, StringComparison.Ordinal));

    /// <summary>Changes the status after validating the transition.</summary>
    internal void TransitionTo(RequestStatus target, DateTimeOffset now)
    {
        RequestStateMachine.EnsureCanTransition(Status, target);
        Status = target;
        UpdatedAt = now;
        if (RequestStateMachine.IsTerminal(target))
        {
            ClosedAt = now;
        }
    }
}
