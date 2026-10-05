using Onboarding.Core.Domain;
using Onboarding.Core.Naming;
using Onboarding.Core.State;
using Onboarding.Core.Workflow;
using Onboarding.Tests.TestSupport;
using static Onboarding.Core.Steps.StepKeys;
using static Onboarding.Tests.TestSupport.RequestFactory;

namespace Onboarding.Tests.Core;

public sealed class RequestWorkflowTests
{
    private readonly RequestFactory _f = new();
    private RequestWorkflow W => _f.Workflow;

    [Fact]
    public void Create_builds_draft_with_checklist_snapshot()
    {
        var (request, audit) = W.Create(RequestType.Onboarding, TestConfig.Person(), Hr, Templates());

        Assert.Equal(RequestStatus.Draft, request.Status);
        Assert.Equal(3, request.Checklist.Count);
        Assert.All(request.Checklist, c => Assert.Equal(request.Id, c.RequestId));
        Assert.Null(request.ConfigSnapshot); // snapshot only at approval
        Assert.Equal(AuditActions.RequestCreated, audit.Action);
        Assert.Equal("hr.user", audit.Actor);
    }

    [Fact]
    public void Offboarding_cannot_be_created_in_v1()
    {
        Assert.Throws<WorkflowException>(() => W.Create(RequestType.Offboarding, TestConfig.Person(), Hr, []));
    }

    [Fact]
    public void Submit_sets_derived_identity()
    {
        var request = _f.Submitted();

        Assert.Equal(RequestStatus.PendingApproval, request.Status);
        Assert.Equal("lirion", request.Derived!.SamAccountName);
    }

    [Fact]
    public void Submit_with_double_name_goes_to_needs_input()
    {
        var request = _f.Submitted(TestConfig.Person("Anna-Lena", "Schmidt"));

        Assert.Equal(RequestStatus.NeedsInput, request.Status);
        Assert.Null(request.Derived);
        Assert.Contains("Doppelname", request.NeedsInputReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Approve_requires_it_admin()
    {
        var request = _f.Submitted();
        var requesterOnly = Actor.Create("someone", Role.Requester);

        Assert.Throws<WorkflowException>(() =>
            W.Approve(request, requesterOnly, TestConfig.Snapshot(), Check(request.Input), [1]));
    }

    [Fact]
    public void Approve_enforces_four_eyes()
    {
        var adminRequester = Actor.Create("it.admin", Role.ITAdmin, Role.Requester);
        var request = W.Create(RequestType.Onboarding, TestConfig.Person(), adminRequester, []).Request;
        W.Submit(request, adminRequester, Check(request.Input));

        Assert.Throws<WorkflowException>(() =>
            W.Approve(request, adminRequester, TestConfig.Snapshot(), Check(request.Input), [1]));
        Assert.Equal(RequestStatus.PendingApproval, request.Status);
    }

    [Fact]
    public void Approve_requires_password()
    {
        var request = _f.Submitted();

        Assert.Throws<WorkflowException>(() => W.Approve(request, Admin, TestConfig.Snapshot(), Check(request.Input), []));
    }

    [Fact]
    public void Approve_freezes_snapshot_and_plans_steps()
    {
        var request = _f.Approved();

        Assert.Equal(RequestStatus.Approved, request.Status);
        Assert.Equal("it.admin", request.ApprovedBy);
        Assert.NotNull(request.ConfigSnapshot);
        Assert.Equal([1, 2, 3], request.EncryptedInitialPassword);
        Assert.Equal(19, request.Steps.Count);
        Assert.Equal(AdCreateUser, request.Steps.OrderBy(s => s.SortOrder).First().StepKey);
    }

    [Fact]
    public void Collision_at_approval_goes_to_needs_input_without_approving()
    {
        var request = _f.Submitted();
        var collision = new Collision(CollisionField.SamAccountName, "lirion", CollisionSource.Directory, "vergeben");
        var recheck = IdentityCheck.From(
            IdentityDeriver.Derive(request.Input, null, TestConfig.Global(), TestConfig.Area(), TestConfig.Department()),
            [collision]);

        var audits = W.Approve(request, Admin, TestConfig.Snapshot(), recheck, [1]);

        Assert.Equal(RequestStatus.NeedsInput, request.Status);
        Assert.Null(request.ApprovedBy);
        Assert.Null(request.EncryptedInitialPassword);
        Assert.Empty(request.Steps);
        Assert.Contains(audits, a => a.Action == AuditActions.NeedsInput);
    }

    [Fact]
    public void Resolve_needs_input_before_approval_returns_to_pending_approval()
    {
        var request = _f.Submitted(TestConfig.Person("Max", "Müller-Lüdenscheidt"));
        var identityOverride = new IdentityOverride("mmueller", "m.mueller-luedenscheidt@cleancontrolling.de");

        var audits = W.ResolveNeedsInput(request, Admin, identityOverride, Check(request.Input, identityOverride));

        Assert.Equal(RequestStatus.PendingApproval, request.Status); // four-eyes still required
        Assert.Equal("mmueller", request.Derived!.SamAccountName);
        var audit = Assert.Single(audits);
        Assert.Contains("neu: sam=mmueller", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_needs_input_after_approval_returns_to_approved_and_audits_old_and_new()
    {
        var request = _f.Approved();
        // Phase 4: AD.CreateUser finds a foreign object with the same sam.
        request.TransitionTo(RequestStatus.Running, _f.Time.GetUtcNow());
        request.TransitionTo(RequestStatus.NeedsInput, _f.Time.GetUtcNow());
        var identityOverride = new IdentityOverride("lirion2", "l.irion@cleancontrolling.de");

        var audits = W.ResolveNeedsInput(request, Admin2, identityOverride, Check(request.Input, identityOverride));

        Assert.Equal(RequestStatus.Approved, request.Status);
        var details = Assert.Single(audits).Details!;
        Assert.Contains("alt: sam=lirion", details, StringComparison.Ordinal);
        Assert.Contains("neu: sam=lirion2", details, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_needs_input_only_by_it_admin()
    {
        var request = _f.Submitted(TestConfig.Person("Anna-Lena", "Schmidt"));
        var identityOverride = new IdentityOverride("aschmidt", "a.schmidt@cleancontrolling.de");

        Assert.Throws<WorkflowException>(() =>
            W.ResolveNeedsInput(request, Hr, identityOverride, Check(request.Input, identityOverride)));
    }

    [Fact]
    public void Resolve_with_invalid_override_stays_in_needs_input()
    {
        var request = _f.Submitted(TestConfig.Person("Anna-Lena", "Schmidt"));
        var identityOverride = new IdentityOverride("a.schmidt", "a.schmidt@cleancontrolling.de");

        var audits = W.ResolveNeedsInput(request, Admin, identityOverride, Check(request.Input, identityOverride));

        Assert.Equal(RequestStatus.NeedsInput, request.Status);
        Assert.Equal(AuditResults.Rejected, Assert.Single(audits).Result);
    }

    [Fact]
    public void Checklist_is_locked_until_ad_create_user_is_done()
    {
        var request = _f.Approved();
        var item = request.Checklist[0];

        Assert.Throws<WorkflowException>(() => W.SetChecklistItemStatus(request, item.Id, ChecklistItemStatus.Done, null, Admin));

        _f.CompleteStep(request, AdCreateUser);
        W.ApplyEvaluation(request);
        W.SetChecklistItemStatus(request, item.Id, ChecklistItemStatus.Done, null, Admin);

        Assert.Equal(ChecklistItemStatus.Done, item.Status);
        Assert.Equal("it.admin", item.CompletedBy);
    }

    [Fact]
    public void Not_applicable_requires_note_and_reset_is_audited()
    {
        var request = _f.Approved();
        _f.CompleteStep(request, AdCreateUser);
        var item = request.Checklist[0];

        Assert.Throws<WorkflowException>(() =>
            W.SetChecklistItemStatus(request, item.Id, ChecklistItemStatus.NotApplicable, " ", Admin));

        W.SetChecklistItemStatus(request, item.Id, ChecklistItemStatus.NotApplicable, "Hat schon Zertifikat", Admin);
        var audits = W.SetChecklistItemStatus(request, item.Id, ChecklistItemStatus.Open, null, Admin2);

        Assert.Equal(ChecklistItemStatus.Open, item.Status);
        Assert.Null(item.CompletedBy);
        Assert.Contains(audits, a => a.Action == AuditActions.ChecklistItemChanged && a.Actor == "it.admin2");
    }

    [Fact]
    public void Requester_may_tick_hr_items_only()
    {
        var templates = new[]
        {
            new ChecklistTemplate { Id = Guid.NewGuid(), Title = "IT-Punkt", Mandatory = true },
            new ChecklistTemplate { Id = Guid.NewGuid(), Title = "HR-Punkt", Mandatory = true, Responsible = ChecklistResponsibility.HR },
        };
        var request = W.Create(RequestType.Onboarding, TestConfig.Person(), Hr, templates).Request;
        W.Submit(request, Hr, Check(request.Input));
        W.Approve(request, Admin, TestConfig.Snapshot(), Check(request.Input), [1]);
        _f.CompleteStep(request, AdCreateUser);
        var it = request.Checklist.Single(c => c.Title == "IT-Punkt");
        var hr = request.Checklist.Single(c => c.Title == "HR-Punkt");
        Assert.Equal(ChecklistResponsibility.HR, hr.Responsible);

        Assert.Throws<WorkflowException>(() => W.SetChecklistItemStatus(request, it.Id, ChecklistItemStatus.Done, null, Hr));
        W.SetChecklistItemStatus(request, hr.Id, ChecklistItemStatus.Done, null, Hr);
        W.SetChecklistItemStatus(request, it.Id, ChecklistItemStatus.Done, null, Admin);

        Assert.Equal("hr.user", hr.CompletedBy);
        Assert.Equal("it.admin", it.CompletedBy);
    }

    [Fact]
    public void Record_directory_object_is_idempotent_and_refuses_other_guid()
    {
        var request = _f.Approved();
        var guid = Guid.NewGuid();

        Assert.Single(W.RecordDirectoryObject(request, guid));
        Assert.Empty(W.RecordDirectoryObject(request, guid));
        Assert.Equal(guid, request.DirectoryObjectGuid);
        Assert.Throws<WorkflowException>(() => W.RecordDirectoryObject(request, Guid.NewGuid()));
    }

    [Fact]
    public void All_steps_done_with_open_mandatory_items_stays_awaiting_checklist() // AK 7
    {
        var request = _f.Approved();
        _f.CompleteAllSteps(request);
        W.ApplyEvaluation(request);

        Assert.Equal(RequestStatus.AwaitingChecklist, request.Status);

        // Optional item alone does not complete the request.
        var optional = request.Checklist.Single(c => !c.Mandatory);
        W.SetChecklistItemStatus(request, optional.Id, ChecklistItemStatus.Done, null, Admin);
        Assert.Equal(RequestStatus.AwaitingChecklist, request.Status);

        var mandatory = request.Checklist.Where(c => c.Mandatory).ToList();
        W.SetChecklistItemStatus(request, mandatory[0].Id, ChecklistItemStatus.Done, null, Admin);
        Assert.Equal(RequestStatus.AwaitingChecklist, request.Status);

        var audits = W.SetChecklistItemStatus(request, mandatory[1].Id, ChecklistItemStatus.NotApplicable, "entfällt", Admin);
        Assert.Equal(RequestStatus.Completed, request.Status);
        Assert.NotNull(request.ClosedAt);
        Assert.Contains(audits, a => a.Action == AuditActions.StatusChanged && a.Actor == Actor.SystemName);
    }

    [Fact]
    public void Failed_step_fails_request_and_retry_resumes()
    {
        var request = _f.Approved();
        _f.CompleteStep(request, AdCreateUser);
        _f.FailStep(request, AdGroups);
        W.ApplyEvaluation(request);
        Assert.Equal(RequestStatus.Failed, request.Status);

        var audits = W.RetryStep(request, AdGroups, Admin);

        Assert.Equal(StepStatus.Pending, request.FindStep(AdGroups)!.Status);
        Assert.Equal(0, request.FindStep(AdGroups)!.Attempts);
        Assert.Equal(RequestStatus.Running, request.Status);
        Assert.Contains(audits, a => a.Action == AuditActions.StepRetry && a.StepKey == AdGroups);
    }

    [Fact]
    public void Mark_failed_step_done_requires_reason_and_can_complete_request()
    {
        var other = _f.Approved();
        _f.CompleteStep(other, AdCreateUser);
        foreach (var item in other.Checklist.Where(c => c.Mandatory))
        {
            W.SetChecklistItemStatus(other, item.Id, ChecklistItemStatus.Done, null, Admin);
        }

        _f.FailStep(other, AdGroups);
        foreach (var step in other.Steps.Where(s => s.Status == StepStatus.Pending).ToList())
        {
            _f.CompleteStep(other, step.StepKey);
        }

        W.ApplyEvaluation(other);
        Assert.Equal(RequestStatus.Failed, other.Status);
        Assert.Throws<WorkflowException>(() => W.MarkStepDone(other, AdGroups, Admin, ""));

        var audits = W.MarkStepDone(other, AdGroups, Admin, "Gruppe manuell zugewiesen");

        Assert.Equal("Gruppe manuell zugewiesen", other.FindStep(AdGroups)!.Note);
        Assert.Equal(RequestStatus.Completed, other.Status); // via AwaitingChecklist
        Assert.Contains(audits, a => a.Details == $"{RequestStatus.Failed} → {RequestStatus.AwaitingChecklist}");
    }

    [Fact]
    public void Manual_task_counts_only_after_admin_marks_it_done()
    {
        // Teams.Voicemail ends as ManualTask (e.g. cmdlet not supported app-only, SPEC §3).
        var fresh = _f.Approved(TestConfig.Person(extension: "12"));
        foreach (var step in fresh.Steps.Where(s => s.Status == StepStatus.Pending && s.StepKey != TeamsVoicemail).ToList())
        {
            _f.CompleteStep(fresh, step.StepKey);
        }

        var voicemail = fresh.FindStep(TeamsVoicemail)!;
        voicemail.TransitionTo(StepStatus.Running, _f.Time.GetUtcNow());
        voicemail.TransitionTo(StepStatus.ManualTask, _f.Time.GetUtcNow());
        W.ApplyEvaluation(fresh);
        Assert.Equal(RequestStatus.Waiting, fresh.Status);

        W.MarkStepDone(fresh, TeamsVoicemail, Admin, "Befehl manuell ausgeführt");

        Assert.Equal(RequestStatus.AwaitingChecklist, fresh.Status);
    }

    [Fact]
    public void Refresh_snapshot_replans_only_unstarted_steps_and_is_audited()
    {
        var request = _f.Approved(TestConfig.Person(extension: "12"));
        _f.CompleteStep(request, AdCreateUser);
        var snapshot = TestConfig.Snapshot(department: d => d.Teams.Voicemail = false);

        var audits = W.RefreshSnapshot(request, Admin, snapshot, Check(request.Input));

        Assert.Equal(StepStatus.Skipped, request.FindStep(TeamsVoicemail)!.Status);
        Assert.Equal(StepStatus.Done, request.FindStep(AdCreateUser)!.Status);
        Assert.False(request.ConfigSnapshot!.Department.Teams.Voicemail);
        var audit = Assert.Single(audits);
        Assert.Equal(AuditActions.SnapshotRefreshed, audit.Action);
        Assert.Contains("\"Voicemail\":true", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"Voicemail\":false", audit.Details, StringComparison.Ordinal);

        // and back: a skipped, never started step becomes pending again
        W.RefreshSnapshot(request, Admin, TestConfig.Snapshot(), Check(request.Input));
        Assert.Equal(StepStatus.Pending, request.FindStep(TeamsVoicemail)!.Status);
    }

    [Fact]
    public void Refresh_snapshot_requires_it_admin_and_approved_request()
    {
        Assert.Throws<WorkflowException>(() =>
            W.RefreshSnapshot(_f.Approved(), Hr, TestConfig.Snapshot(), Check(TestConfig.Person())));
        Assert.Throws<WorkflowException>(() =>
            W.RefreshSnapshot(_f.Submitted(), Admin, TestConfig.Snapshot(), Check(TestConfig.Person())));
    }

    [Fact]
    public void Requester_can_cancel_own_unapproved_request_only()
    {
        var pending = _f.Submitted();
        W.Cancel(pending, Hr, "doch nicht");
        Assert.Equal(RequestStatus.Cancelled, pending.Status);

        var approved = _f.Approved();
        Assert.Throws<WorkflowException>(() => W.Cancel(approved, Hr, "doch nicht"));
        W.Cancel(approved, Admin, "doch nicht");
        Assert.Equal(RequestStatus.Cancelled, approved.Status);

        Assert.Throws<InvalidStateTransitionException>(() => W.Cancel(approved, Admin, "nochmal"));
    }

    [Fact]
    public void Initial_password_is_cleared_only_after_create_user()
    {
        var request = _f.Approved();

        Assert.Throws<WorkflowException>(() => RequestWorkflow.ClearInitialPassword(request));

        _f.CompleteStep(request, AdCreateUser);
        RequestWorkflow.ClearInitialPassword(request);

        Assert.Null(request.EncryptedInitialPassword);
    }

    [Fact]
    public void Evaluation_waits_for_entry_date_step()
    {
        var request = _f.Approved();
        foreach (var step in request.Steps.Where(s => s.Status == StepStatus.Pending && s.StepKey != AdEnable).ToList())
        {
            _f.CompleteStep(request, step.StepKey);
        }

        W.ApplyEvaluation(request);

        Assert.Equal(RequestStatus.Running, request.Status);
        Assert.Equal(StepStatus.Pending, request.FindStep(AdEnable)!.Status);
    }
}
