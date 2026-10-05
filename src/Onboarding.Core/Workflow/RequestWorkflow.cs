using System.Text.Json;
using Onboarding.Core.Checklists;
using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Core.Naming;
using Onboarding.Core.State;
using Onboarding.Core.Steps;

namespace Onboarding.Core.Workflow;

/// <summary>
/// User and system actions on requests. Pure domain logic: inputs that need I/O (identity check,
/// config snapshot, encrypted password) are passed in; returned audit entries must be persisted
/// together with the request in the same transaction.
/// </summary>
public sealed class RequestWorkflow(TimeProvider timeProvider, StepPlanRegistry stepPlans)
{
    private static readonly JsonSerializerOptions AuditJson = new() { WriteIndented = false };

    public RequestWorkflow(TimeProvider timeProvider)
        : this(timeProvider, StepPlanRegistry.Default)
    {
    }

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>Creates a draft and its checklist from the templates (SPEC §6.2: at creation).</summary>
    public (Request Request, AuditEntry Audit) Create(
        RequestType type,
        PersonInput input,
        Actor actor,
        IEnumerable<ChecklistTemplate> checklistTemplates)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actor);
        Require(actor.IsRequester || actor.IsITAdmin, "Nur Requester oder ITAdmin dürfen Aufträge anlegen.");
        if (!stepPlans.Supports(type))
        {
            throw new WorkflowException($"Auftragstyp {type} wird in dieser Version nicht unterstützt.");
        }

        var now = Now;
        var request = new Request
        {
            Id = Guid.NewGuid(),
            Type = type,
            Input = input,
            CreatedBy = actor.Name,
            CreatedAt = now,
        };
        request.UpdatedAt = now;
        request.Checklist.AddRange(ChecklistComposer.Compose(
            request.Id, type, input.AreaId, input.DepartmentId, checklistTemplates));

        return (request, Audit(actor, request, AuditActions.RequestCreated, AuditResults.Success,
            details: $"Typ {type}, {request.Checklist.Count} Checklistenpunkte."));
    }

    /// <summary>Draft → PendingApproval, or NeedsInput if derivation/collision check failed.</summary>
    public IReadOnlyList<AuditEntry> Submit(Request request, Actor actor, IdentityCheck check)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(check);
        Require(actor.IsITAdmin || actor.Name == request.CreatedBy, "Nur Antragsteller oder ITAdmin dürfen einreichen.");

        var now = Now;
        request.TransitionTo(RequestStatus.PendingApproval, now);
        var audits = new List<AuditEntry> { Audit(actor, request, AuditActions.RequestSubmitted, AuditResults.Success) };
        ApplyIdentityCheck(request, check, actor, now, audits);
        return audits;
    }

    /// <summary>
    /// PendingApproval → Approved (four-eyes, ITAdmin). Freezes the config snapshot and creates the
    /// steps. If the re-check finds issues the request goes to NeedsInput and is not approved.
    /// </summary>
    public IReadOnlyList<AuditEntry> Approve(
        Request request,
        Actor approver,
        RequestConfigSnapshot snapshot,
        IdentityCheck recheck,
        byte[] encryptedInitialPassword)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(recheck);
        Require(approver.IsITAdmin, "Freigabe nur durch ITAdmin.");
        Require(!string.Equals(approver.Name, request.CreatedBy, StringComparison.OrdinalIgnoreCase),
            "Vier-Augen-Prinzip: Antragsteller darf nicht freigeben.");
        Require(request.Status == RequestStatus.PendingApproval, $"Freigabe im Status {request.Status} nicht möglich.");
        Require(encryptedInitialPassword is { Length: > 0 }, "Startpasswort fehlt.");

        var now = Now;
        var audits = new List<AuditEntry>();
        if (!ApplyIdentityCheck(request, recheck, approver, now, audits))
        {
            return audits;
        }

        request.ConfigSnapshot = snapshot;
        request.EncryptedInitialPassword = encryptedInitialPassword;
        request.ApprovedBy = approver.Name;
        request.ApprovedAt = now;
        request.TransitionTo(RequestStatus.Approved, now);
        PlanSteps(request, snapshot, now);

        audits.Add(Audit(approver, request, AuditActions.RequestApproved, AuditResults.Success,
            details: $"{request.Steps.Count(s => s.Status == StepStatus.Pending)} Steps geplant, " +
                     $"{request.Steps.Count(s => s.Status == StepStatus.Skipped)} übersprungen."));
        return audits;
    }

    /// <summary>
    /// NeedsInput → Approved (if already approved) or PendingApproval, with manually assigned
    /// sam/mail. ITAdmin only; old/new values are audited (DECISIONS S1).
    /// </summary>
    public IReadOnlyList<AuditEntry> ResolveNeedsInput(
        Request request,
        Actor admin,
        IdentityOverride identityOverride,
        IdentityCheck recheck)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(identityOverride);
        ArgumentNullException.ThrowIfNull(recheck);
        Require(admin.IsITAdmin, "Nur ITAdmin darf Namen manuell vergeben.");
        Require(request.Status == RequestStatus.NeedsInput, $"Auftrag ist nicht im Status {RequestStatus.NeedsInput}.");

        var now = Now;
        if (recheck.NeedsInput)
        {
            return [Audit(admin, request, AuditActions.IdentityResolved, AuditResults.Rejected, details: recheck.Summary)];
        }

        var old = $"sam={request.Derived?.SamAccountName ?? request.IdentityOverride?.SamAccountName ?? "-"}, " +
                  $"mail={request.Derived?.Mail ?? request.IdentityOverride?.Mail ?? request.Input.Mail}";
        request.IdentityOverride = identityOverride;
        request.Derived = recheck.Identity;
        request.NeedsInputReason = null;
        request.TransitionTo(request.ApprovedBy is null ? RequestStatus.PendingApproval : RequestStatus.Approved, now);

        return
        [
            Audit(admin, request, AuditActions.IdentityResolved, AuditResults.Success,
                details: $"alt: {old}; neu: sam={recheck.Identity!.SamAccountName}, mail={recheck.Identity.Mail}; Status {request.Status}"),
        ];
    }

    /// <summary>
    /// Replaces the config snapshot of an approved request (ITAdmin, DECISIONS S3). Steps that
    /// have not started yet are re-planned; completed steps are not touched.
    /// </summary>
    public IReadOnlyList<AuditEntry> RefreshSnapshot(
        Request request,
        Actor admin,
        RequestConfigSnapshot snapshot,
        IdentityCheck recheck)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(recheck);
        Require(admin.IsITAdmin, "Nur ITAdmin darf den Konfig-Snapshot aktualisieren.");
        Require(request.ApprovedBy is not null && !request.IsClosed,
            $"Snapshot-Aktualisierung im Status {request.Status} nicht möglich.");

        var now = Now;
        if (recheck.NeedsInput)
        {
            return [Audit(admin, request, AuditActions.SnapshotRefreshed, AuditResults.Rejected, details: recheck.Summary)];
        }

        var oldJson = JsonSerializer.Serialize(request.ConfigSnapshot, AuditJson);
        var newJson = JsonSerializer.Serialize(snapshot, AuditJson);
        request.ConfigSnapshot = snapshot;
        request.Derived = recheck.Identity;
        request.UpdatedAt = now;
        var replanned = PlanSteps(request, snapshot, now);

        return
        [
            Audit(admin, request, AuditActions.SnapshotRefreshed, AuditResults.Success,
                details: $"alt: {oldJson}; neu: {newJson}; neu geplant: {string.Join(", ", replanned)}"),
        ];
    }

    public IReadOnlyList<AuditEntry> Cancel(Request request, Actor actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        Require(!string.IsNullOrWhiteSpace(reason), "Begründung erforderlich.");
        var ownUnapproved = actor.Name == request.CreatedBy &&
                            request.Status is RequestStatus.Draft or RequestStatus.PendingApproval;
        Require(actor.IsITAdmin || ownUnapproved, "Abbrechen nur durch ITAdmin oder Antragsteller vor der Freigabe.");

        request.TransitionTo(RequestStatus.Cancelled, Now);
        return [Audit(actor, request, AuditActions.RequestCancelled, AuditResults.Success, details: reason.Trim())];
    }

    /// <summary>Failed step → Pending with reset attempts (SPEC §6 "Retry").</summary>
    public IReadOnlyList<AuditEntry> RetryStep(Request request, string stepKey, Actor admin)
    {
        var step = AdminStep(request, stepKey, admin);
        Require(step.Status == StepStatus.Failed, $"Retry nur für fehlgeschlagene Steps (Status {step.Status}).");

        var now = Now;
        step.TransitionTo(StepStatus.Pending, now);
        step.Attempts = 0;
        step.FirstAttemptAt = null;
        step.NextAttemptAt = null;

        var audits = new List<AuditEntry> { Audit(admin, request, AuditActions.StepRetry, AuditResults.Success, stepKey) };
        audits.AddRange(ApplyEvaluation(request));
        return audits;
    }

    /// <summary>Failed or ManualTask step → Done with mandatory reason (SPEC §6).</summary>
    public IReadOnlyList<AuditEntry> MarkStepDone(Request request, string stepKey, Actor admin, string reason)
    {
        var step = AdminStep(request, stepKey, admin);
        Require(!string.IsNullOrWhiteSpace(reason), "Begründung erforderlich.");
        Require(step.Status is StepStatus.Failed or StepStatus.ManualTask,
            $"Als erledigt markieren nur für Failed/ManualTask (Status {step.Status}).");

        step.TransitionTo(StepStatus.Done, Now);
        step.Note = reason.Trim();

        var audits = new List<AuditEntry>
        {
            Audit(admin, request, AuditActions.StepMarkedDone, AuditResults.Success, stepKey, reason.Trim()),
        };
        audits.AddRange(ApplyEvaluation(request));
        return audits;
    }

    /// <summary>
    /// Ticks, resets or marks a checklist item as not applicable (SPEC §6.2). Allowed once the
    /// account exists (<c>AD.CreateUser</c> done); NotApplicable requires a note.
    /// </summary>
    public IReadOnlyList<AuditEntry> SetChecklistItemStatus(
        Request request,
        Guid itemId,
        ChecklistItemStatus status,
        string? note,
        Actor actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        Require(actor.IsITAdmin, "Checkliste nur durch ITAdmin.");
        Require(!request.IsClosed, $"Auftrag ist abgeschlossen ({request.Status}).");
        Require(CanEditChecklist(request), "Checkliste ist erst abhakbar, wenn AD.CreateUser erledigt ist.");
        Require(status != ChecklistItemStatus.NotApplicable || !string.IsNullOrWhiteSpace(note),
            "„Nicht zutreffend“ erfordert eine Notiz.");

        var item = request.Checklist.SingleOrDefault(c => c.Id == itemId)
                   ?? throw new WorkflowException("Checklistenpunkt nicht gefunden.");
        var old = item.Status;
        item.SetStatus(status, note, actor.Name, Now);
        request.UpdatedAt = Now;

        var audits = new List<AuditEntry>
        {
            Audit(actor, request, AuditActions.ChecklistItemChanged, AuditResults.Success,
                details: $"'{item.Title}': {old} → {status}" + (item.Note is null ? "" : $"; Notiz: {item.Note}")),
        };
        audits.AddRange(ApplyEvaluation(request));
        return audits;
    }

    public static bool CanEditChecklist(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.FindStep(StepKeys.AdCreateUser)?.Status == StepStatus.Done;
    }

    /// <summary>
    /// Moves the request to the status derived by <see cref="RequestStatusEvaluator"/>, via
    /// intermediate statuses where no direct transition exists (e.g. Failed → AwaitingChecklist →
    /// Completed). Called by the worker after step changes and by the admin actions above.
    /// </summary>
    public IReadOnlyList<AuditEntry> ApplyEvaluation(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = RequestStatusEvaluator.Evaluate(request);
        if (target == request.Status)
        {
            return [];
        }

        var now = Now;
        var audits = new List<AuditEntry>();
        foreach (var next in PathTo(request.Status, target))
        {
            var from = request.Status;
            request.TransitionTo(next, now);
            audits.Add(Audit(Actor.System, request, AuditActions.StatusChanged, AuditResults.Success, details: $"{from} → {next}"));
        }

        return audits;
    }

    /// <summary>Removes the encrypted initial password once <c>AD.CreateUser</c> is done (SPEC §9).</summary>
    public static void ClearInitialPassword(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.FindStep(StepKeys.AdCreateUser)?.Status == StepStatus.Done,
            "Startpasswort wird erst nach AD.CreateUser gelöscht.");
        request.EncryptedInitialPassword = null;
    }

    private bool ApplyIdentityCheck(Request request, IdentityCheck check, Actor actor, DateTimeOffset now, List<AuditEntry> audits)
    {
        if (check.NeedsInput)
        {
            request.Derived = null;
            request.NeedsInputReason = check.Summary;
            request.TransitionTo(RequestStatus.NeedsInput, now);
            audits.Add(Audit(actor, request, AuditActions.NeedsInput, AuditResults.Rejected, details: check.Summary));
            return false;
        }

        request.Derived = check.Identity;
        request.NeedsInputReason = null;
        return true;
    }

    /// <summary>Creates missing steps and re-plans steps that have not started. Returns re-planned keys.</summary>
    private List<string> PlanSteps(Request request, RequestConfigSnapshot snapshot, DateTimeOffset now)
    {
        var context = new StepPlanContext(request.Input, snapshot);
        var replanned = new List<string>();
        var order = 0;
        foreach (var definition in stepPlans.For(request.Type).Steps)
        {
            var step = request.FindStep(definition.Key);
            if (step is null)
            {
                step = new RequestStep { Id = Guid.NewGuid(), RequestId = request.Id, StepKey = definition.Key };
                request.Steps.Add(step);
            }
            else if (step.Attempts > 0 || step.Status is not (StepStatus.Pending or StepStatus.Skipped))
            {
                order++;
                continue;
            }

            step.SortOrder = order++;
            var skipReason = definition.SkipReason?.Invoke(context);
            step.InitializeAs(skipReason is null ? StepStatus.Pending : StepStatus.Skipped);
            step.Note = skipReason;
            step.CompletedAt = skipReason is null ? null : now;
            step.NextAttemptAt = definition.NotBefore?.Invoke(context);
            replanned.Add(definition.Key);
        }

        return replanned;
    }

    private static RequestStep AdminStep(Request request, string stepKey, Actor admin)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(admin);
        Require(admin.IsITAdmin, "Nur ITAdmin.");
        Require(!request.IsClosed, $"Auftrag ist abgeschlossen ({request.Status}).");
        return request.FindStep(stepKey) ?? throw new WorkflowException($"Step {stepKey} nicht gefunden.");
    }

    /// <summary>Shortest transition path (BFS) avoiding NeedsInput/Cancelled detours.</summary>
    private static List<RequestStatus> PathTo(RequestStatus from, RequestStatus to)
    {
        var previous = new Dictionary<RequestStatus, RequestStatus>();
        var queue = new Queue<RequestStatus>([from]);
        var visited = new HashSet<RequestStatus> { from };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == to)
            {
                var path = new List<RequestStatus>();
                for (var s = to; s != from; s = previous[s])
                {
                    path.Add(s);
                }

                path.Reverse();
                return path;
            }

            foreach (var next in RequestStateMachine.AllowedTargets(current))
            {
                if (next is RequestStatus.NeedsInput or RequestStatus.Cancelled or RequestStatus.PendingApproval ||
                    !visited.Add(next))
                {
                    continue;
                }

                previous[next] = current;
                queue.Enqueue(next);
            }
        }

        throw new InvalidStateTransitionException($"No transition path from {from} to {to}.");
    }

    private AuditEntry Audit(
        Actor actor,
        Request request,
        string action,
        string result,
        string? stepKey = null,
        string? details = null) =>
        AuditEntry.Create(Now, actor.Name, action, result, request.Id, stepKey, details);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new WorkflowException(message);
        }
    }
}

/// <summary>Action not allowed (role, four-eyes, status). Message is user-facing (German).</summary>
public sealed class WorkflowException : InvalidOperationException
{
    public WorkflowException()
    {
    }

    public WorkflowException(string message)
        : base(message)
    {
    }

    public WorkflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
