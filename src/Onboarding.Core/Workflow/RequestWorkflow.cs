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

    /// <summary>
    /// Changes the input of a draft. If area or department changed, the checklist is composed
    /// again (the draft has not been submitted, so this still counts as "at creation").
    /// </summary>
    public IReadOnlyList<AuditEntry> UpdateDraft(
        Request request,
        Actor actor,
        PersonInput input,
        IEnumerable<ChecklistTemplate> checklistTemplates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(input);
        Require(request.Status == RequestStatus.Draft, "Nur Entwürfe können bearbeitet werden.");
        Require(actor.IsITAdmin || actor.Name == request.CreatedBy, "Nur Antragsteller oder ITAdmin dürfen den Entwurf bearbeiten.");

        var scopeChanged = request.Input.AreaId != input.AreaId || request.Input.DepartmentId != input.DepartmentId;
        request.Input = input;
        request.UpdatedAt = Now;
        if (scopeChanged)
        {
            request.Checklist.Clear();
            request.Checklist.AddRange(ChecklistComposer.Compose(
                request.Id, request.Type, input.AreaId, input.DepartmentId, checklistTemplates));
        }

        return [Audit(actor, request, AuditActions.DraftUpdated, AuditResults.Success,
            details: scopeChanged ? "Bereich/Abteilung geändert, Checkliste neu erstellt." : null)];
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
        byte[] encryptedInitialPassword,
        string? selfApprovalReason = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(recheck);
        Require(approver.IsITAdmin, "Freigabe nur durch ITAdmin.");
        var self = IsCreator(request, approver);
        var reason = selfApprovalReason?.Trim();
        if (self)
        {
            // DECISIONS A1: four-eyes unless the policy allows an audited self-approval.
            Require(snapshot.Global.ApprovalPolicy == ApprovalPolicy.SelfApprovalWithReason,
                "Vier-Augen-Prinzip: Antragsteller darf nicht freigeben.");
            Require(reason is { Length: >= MinSelfApprovalReasonLength },
                $"Selbstfreigabe: Begründung erforderlich (mindestens {MinSelfApprovalReasonLength} Zeichen).");
        }

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
        request.SelfApproved = self;
        request.SelfApprovalReason = self ? reason : null;
        request.TransitionTo(RequestStatus.Approved, now);
        PlanSteps(request, snapshot, now);

        audits.Add(Audit(approver, request, AuditActions.RequestApproved, AuditResults.Success,
            details: $"{request.Steps.Count(s => s.Status == StepStatus.Pending)} Steps geplant, " +
                     $"{request.Steps.Count(s => s.Status == StepStatus.Skipped)} übersprungen."));
        if (self)
        {
            audits.Add(Audit(approver, request, AuditActions.SelfApproved, AuditResults.Success, details: "Selbstfreigabe: " + reason));
        }

        return audits;
    }

    /// <summary>Minimum length of a self-approval reason (DECISIONS A1).</summary>
    public const int MinSelfApprovalReasonLength = 10;

    /// <summary>True if the actor created the request (four-eyes / self-approval check).</summary>
    public static bool IsCreator(Request request, Actor actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        return string.Equals(actor.Name, request.CreatedBy, StringComparison.OrdinalIgnoreCase);
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
        foreach (var step in request.Steps.Where(s => s.Status == StepStatus.NeedsInput))
        {
            ResetForRerun(step);
        }

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
        var audits = new List<AuditEntry> { Audit(actor, request, AuditActions.RequestCancelled, AuditResults.Success, details: reason.Trim()) };
        audits.AddRange(DeleteInitialPassword(request, "Auftrag abgebrochen"));
        return audits;
    }

    /// <summary>Failed or NeedsInput step → Pending with reset attempts (SPEC §6 "Retry").</summary>
    public IReadOnlyList<AuditEntry> RetryStep(Request request, string stepKey, Actor admin)
    {
        var step = AdminStep(request, stepKey, admin);
        Require(step.Status is StepStatus.Failed or StepStatus.NeedsInput,
            $"Retry nur für Steps in Failed/NeedsInput (Status {step.Status}).");

        ResetForRerun(step);
        var audits = new List<AuditEntry> { Audit(admin, request, AuditActions.StepRetry, AuditResults.Success, stepKey) };
        audits.AddRange(ApplyEvaluation(request));
        return audits;
    }

    /// <summary>
    /// "Überschreiben" (DECISIONS L3): NeedsInput step → Pending; the next run may replace the
    /// existing target (e.g. a logon script changed by hand). ITAdmin only, with reason.
    /// </summary>
    public IReadOnlyList<AuditEntry> ForceStep(Request request, string stepKey, Actor admin, string reason)
    {
        var step = AdminStep(request, stepKey, admin);
        Require(!string.IsNullOrWhiteSpace(reason), "Begründung erforderlich.");
        Require(step.Status == StepStatus.NeedsInput, $"Überschreiben nur für Steps in NeedsInput (Status {step.Status}).");

        ResetForRerun(step);
        step.ForceRequested = true;
        var audits = new List<AuditEntry> { Audit(admin, request, AuditActions.StepForced, AuditResults.Success, stepKey, reason.Trim()) };
        audits.AddRange(ApplyEvaluation(request));
        return audits;
    }

    /// <summary>
    /// Worker: claims a due step (→ Running). Audited only when the step starts its run
    /// (Pending → Running); retries of a waiting step are logged by the worker, not audited (P3).
    /// The caller saves with the step's concurrency token, so two workers cannot claim the same step.
    /// </summary>
    public IReadOnlyList<AuditEntry> ClaimStep(Request request, RequestStep step)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(step);
        Require(request.Steps.Contains(step), "Step gehört nicht zum Auftrag.");
        Require(request.Steps.All(s => s.Status != StepStatus.Running), "Pro Auftrag läuft höchstens ein Step gleichzeitig.");

        var now = Now;
        var starting = step.Status == StepStatus.Pending;
        step.TransitionTo(StepStatus.Running, now);
        step.Attempts++;
        step.FirstAttemptAt ??= now;
        request.UpdatedAt = now;

        var audits = new List<AuditEntry>();
        if (starting)
        {
            audits.Add(Audit(Actor.System, request, AuditActions.StepStarted, AuditResults.Success, step.StepKey,
                step.ForceRequested ? "mit Überschreiben" : null));
        }

        audits.AddRange(ApplyEvaluation(request));
        return audits;
    }

    /// <summary>
    /// Worker: applies an executor outcome to a Running step: status, backoff and step timeout
    /// (SPEC §6), output, objectGUID, initial password deletion, request status.
    /// </summary>
    public IReadOnlyList<AuditEntry> ApplyStepOutcome(Request request, string stepKey, StepOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outcome);
        var step = request.FindStep(stepKey) ?? throw new WorkflowException($"Step {stepKey} nicht gefunden.");
        Require(step.Status == StepStatus.Running, $"Step {stepKey} läuft nicht (Status {step.Status}).");
        var execution = request.ConfigSnapshot?.Global.Execution
                        ?? throw new WorkflowException("Auftrag hat keinen Konfig-Snapshot.");

        var now = Now;
        var audits = new List<AuditEntry>();
        var attempts = $"Versuche: {step.Attempts}";
        step.ForceRequested = false;
        request.UpdatedAt = now;

        var kind = outcome.Kind;
        var message = outcome.Message;
        var code = ReasonCodes.IsValid(outcome.Code) ? outcome.Code : null;
        var policy = BackoffPolicy.FromConfig(execution);
        if (kind == StepOutcomeKind.Waiting && policy.IsTimedOut(step.FirstAttemptAt ?? now, now))
        {
            kind = StepOutcomeKind.Failed;
            message = $"Timeout nach {policy.Timeout} ohne Erfolg. Letzter Stand: {outcome.Message}";
            code = ReasonCodes.StepTimeout;
        }

        step.ReasonCode = kind == StepOutcomeKind.Done ? null : code;
        var codeNote = code is null ? null : $"Code: {code}";

        switch (kind)
        {
            case StepOutcomeKind.Done:
                step.TransitionTo(StepStatus.Done, now);
                step.OutputJson = outcome.OutputJson;
                step.LastError = null;
                step.WaitingSince = null;
                audits.Add(Audit(Actor.System, request, AuditActions.StepFinished, nameof(StepStatus.Done), stepKey,
                    Join(message, outcome.OutputJson, attempts)));
                if (outcome.DirectoryObjectGuid is { } guid)
                {
                    audits.AddRange(RecordDirectoryObject(request, guid, outcome.DirectoryObjectSid));
                }

                if (stepKey == StepKeys.AdCreateUser)
                {
                    audits.AddRange(DeleteInitialPassword(request, "AD.CreateUser erledigt"));
                }

                break;

            case StepOutcomeKind.Waiting:
                step.TransitionTo(StepStatus.Waiting, now);
                step.LastError = null;
                step.Note = message;
                step.NextAttemptAt = now + policy.DelayAfterAttempt(step.Attempts);
                if (step.WaitingSince is null)
                {
                    step.WaitingSince = now;
                    audits.Add(Audit(Actor.System, request, AuditActions.StepWaiting, nameof(StepStatus.Waiting), stepKey, Join(message, codeNote)));
                }

                break;

            case StepOutcomeKind.Skipped:
                step.TransitionTo(StepStatus.Skipped, now);
                step.Note = message;
                step.WaitingSince = null;
                audits.Add(Audit(Actor.System, request, AuditActions.StepFinished, nameof(StepStatus.Skipped), stepKey, Join(message, codeNote, attempts)));
                break;

            case StepOutcomeKind.Failed:
            case StepOutcomeKind.NeedsInput:
            case StepOutcomeKind.ManualTask:
                var target = kind switch
                {
                    StepOutcomeKind.Failed => StepStatus.Failed,
                    StepOutcomeKind.NeedsInput => StepStatus.NeedsInput,
                    _ => StepStatus.ManualTask,
                };
                step.TransitionTo(target, now);
                if (kind == StepOutcomeKind.ManualTask)
                {
                    step.Note = message;
                }
                else
                {
                    step.LastError = message;
                }

                step.WaitingSince = null;
                audits.Add(Audit(Actor.System, request, AuditActions.StepFinished,
                    kind == StepOutcomeKind.Failed ? AuditResults.Failed : target.ToString(), stepKey, Join(message, codeNote, attempts)));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), kind, "Unknown outcome.");
        }

        audits.AddRange(ApplyEvaluation(request));
        return audits;
    }

    /// <summary>
    /// Worker start: a step left Running by a crash becomes Waiting and due immediately. Safe
    /// because every step is idempotent (SPEC §6).
    /// </summary>
    public IReadOnlyList<AuditEntry> RecoverInterruptedStep(Request request, RequestStep step)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(step);
        Require(step.Status == StepStatus.Running, $"Step {step.StepKey} läuft nicht.");
        var now = Now;
        step.TransitionTo(StepStatus.Waiting, now);
        step.NextAttemptAt = now;
        step.WaitingSince ??= now;
        var audits = new List<AuditEntry>
        {
            Audit(Actor.System, request, AuditActions.StepRecovered, AuditResults.Success, step.StepKey,
                "Worker wurde während der Ausführung beendet; Step wird erneut ausgeführt."),
        };
        audits.AddRange(ApplyEvaluation(request));
        return audits;
    }

    /// <summary>Failed or ManualTask step → Done with mandatory reason (SPEC §6).</summary>
    public IReadOnlyList<AuditEntry> MarkStepDone(Request request, string stepKey, Actor admin, string reason)
    {
        var step = AdminStep(request, stepKey, admin);
        Require(!string.IsNullOrWhiteSpace(reason), "Begründung erforderlich.");
        Require(step.Status is StepStatus.Failed or StepStatus.ManualTask or StepStatus.NeedsInput,
            $"Als erledigt markieren nur für Failed/ManualTask/NeedsInput (Status {step.Status}).");

        step.TransitionTo(StepStatus.Done, Now);
        step.Note = reason.Trim();
        step.WaitingSince = null;

        var audits = new List<AuditEntry>
        {
            Audit(admin, request, AuditActions.StepMarkedDone, AuditResults.Success, stepKey, reason.Trim()),
        };
        if (stepKey == StepKeys.AdCreateUser)
        {
            audits.AddRange(DeleteInitialPassword(request, "AD.CreateUser als erledigt markiert"));
        }

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
        Require(!request.IsClosed, $"Auftrag ist abgeschlossen ({request.Status}).");
        Require(CanEditChecklist(request), "Checkliste ist erst abhakbar, wenn AD.CreateUser erledigt ist.");
        Require(status != ChecklistItemStatus.NotApplicable || !string.IsNullOrWhiteSpace(note),
            "„Nicht zutreffend“ erfordert eine Notiz.");

        var item = request.Checklist.SingleOrDefault(c => c.Id == itemId)
                   ?? throw new WorkflowException("Checklistenpunkt nicht gefunden.");
        Require(CanTickChecklistItem(actor, item), "Requester dürfen nur HR-Punkte abhaken.");
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

    /// <summary>ITAdmin may tick all items, requesters only HR items.</summary>
    public static bool CanTickChecklistItem(Actor actor, ChecklistItem item)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(item);
        return actor.IsITAdmin || (actor.IsRequester && item.Responsible == ChecklistResponsibility.HR);
    }

    /// <summary>
    /// Stores the objectGUID of the account created by <c>AD.CreateUser</c> (called by the
    /// worker, DECISIONS K6). Setting a different GUID later is refused.
    /// </summary>
    /// <remarks>
    /// The objectSid is recorded alongside (DECISIONS X14) – also later for an already recorded
    /// GUID. A malformed SID is ignored; a different SID for the same account is refused.
    /// </remarks>
    public IReadOnlyList<AuditEntry> RecordDirectoryObject(Request request, Guid objectGuid, string? objectSid = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(objectGuid != Guid.Empty, "objectGUID fehlt.");
        Require(request.DirectoryObjectGuid is null || request.DirectoryObjectGuid == objectGuid,
            $"Auftrag ist bereits dem Konto {request.DirectoryObjectGuid} zugeordnet.");
        var sid = DirectorySid.IsValid(objectSid) ? objectSid : null;
        Require(sid is null || request.DirectoryObjectSid is null || string.Equals(request.DirectoryObjectSid, sid, StringComparison.OrdinalIgnoreCase),
            $"Auftrag ist bereits der SID {request.DirectoryObjectSid} zugeordnet.");

        var details = new List<string>();
        if (request.DirectoryObjectGuid is null)
        {
            request.DirectoryObjectGuid = objectGuid;
            details.Add($"objectGUID {objectGuid}");
        }

        if (sid is not null && request.DirectoryObjectSid is null)
        {
            request.DirectoryObjectSid = sid;
            details.Add($"objectSid {sid}");
        }

        if (details.Count == 0)
        {
            return [];
        }

        request.UpdatedAt = Now;
        return
        [
            Audit(Actor.System, request, AuditActions.DirectoryObjectRecorded, AuditResults.Success,
                StepKeys.AdCreateUser, string.Join(", ", details)),
        ];
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

    /// <summary>
    /// Removes the encrypted initial password (SPEC §9): once <c>AD.CreateUser</c> is done or the
    /// request is cancelled (P4). While AD.CreateUser is in NeedsInput it stays.
    /// </summary>
    private List<AuditEntry> DeleteInitialPassword(Request request, string reason)
    {
        if (request.EncryptedInitialPassword is null)
        {
            return [];
        }

        request.EncryptedInitialPassword = null;
        return [Audit(Actor.System, request, AuditActions.InitialPasswordDeleted, AuditResults.Success, details: reason)];
    }

    private static void ResetForRerun(RequestStep step)
    {
        step.TransitionTo(StepStatus.Pending, default);
        step.Attempts = 0;
        step.FirstAttemptAt = null;
        step.NextAttemptAt = null;
        step.WaitingSince = null;
        step.ForceRequested = false;
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join("; ", present);
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
                var detour = next is RequestStatus.NeedsInput or RequestStatus.Cancelled or RequestStatus.PendingApproval;
                if ((detour && next != to) || !visited.Add(next))
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
