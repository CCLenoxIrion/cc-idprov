using Onboarding.Core.Domain;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;
using Onboarding.Web.Services;
using static Onboarding.Core.Steps.StepKeys;

namespace Onboarding.Tests.Web;

public sealed class RequestServiceTests : IDisposable
{
    private const string Password = "Sommer2026!xyz";
    private readonly WebTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<(Guid DepartmentId, Guid RequestId)> SubmittedAsync(string first = "Lenox", string last = "Irion", string? mail = null, string? extension = "12")
    {
        var departmentId = await _host.CreateDepartmentAsync();
        _host.SignInHr();
        var id = await _host.Requests.CreateAsync(WebTestHost.Input(departmentId, first, last, mail, extension), submit: true);
        return (departmentId, id);
    }

    [Fact]
    public async Task Preview_shows_derived_values_warnings_and_licenses()
    {
        var departmentId = await _host.CreateDepartmentAsync(phoneExpected: false);
        _host.SignInHr();

        var preview = await _host.Requests.PreviewAsync(null, WebTestHost.Input(departmentId));

        Assert.Equal("lirion", preview.Identity!.SamAccountName);
        Assert.Empty(preview.Issues);
        Assert.Contains(preview.Warnings, w => w.Contains("normalerweise keine Telefonie", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, w => w.Contains("MCOEV", StringComparison.Ordinal)); // 0 free
        Assert.Equal([new LicenseRow("SPB", 5), new LicenseRow("MCOEV", 0)], preview.Licenses);
    }

    [Fact]
    public async Task Preview_reports_extension_in_use()
    {
        var departmentId = await _host.CreateDepartmentAsync();
        _host.SignInHr();

        var preview = await _host.Requests.PreviewAsync(null, WebTestHost.Input(departmentId, extension: "1"));

        Assert.Contains(preview.Issues, i => i.CollisionField == CollisionField.PhoneNumber);
    }

    [Fact]
    public async Task Create_and_submit_persists_request_checklist_and_audit()
    {
        var (_, id) = await SubmittedAsync();
        _host.SignInAdmin();

        var details = (await _host.Requests.GetDetailsAsync(id))!;

        Assert.Equal(RequestStatus.PendingApproval, details.Request.Status);
        Assert.Equal("hr.mueller", details.Request.CreatedBy);
        Assert.Equal(7, details.Request.Checklist.Count);
        Assert.Equal("Petra Vogel", details.Manager!.DisplayName);
        Assert.Equal([AuditActions.RequestCreated, AuditActions.RequestSubmitted], details.Audit.Select(a => a.Action));
    }

    [Fact]
    public async Task Collision_with_directory_at_submit_needs_input()
    {
        var departmentId = await _host.CreateDepartmentAsync();
        _host.SignInHr();

        var id = await _host.Requests.CreateAsync(WebTestHost.Input(departmentId, "Martin", "Schmidt", extension: null), submit: true);
        _host.SignInAdmin();

        var details = (await _host.Requests.GetDetailsAsync(id))!;
        Assert.Equal(RequestStatus.NeedsInput, details.Request.Status);
        Assert.Contains("mschmidt", details.Request.NeedsInputReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_by_other_admin_encrypts_password_and_plans_steps()
    {
        var (_, id) = await SubmittedAsync();
        _host.SignInAdmin("it.admin2");

        await _host.Requests.ApproveAsync(id, new SecretString(Password));

        var details = (await _host.Requests.GetDetailsAsync(id))!;
        Assert.Equal(RequestStatus.Approved, details.Request.Status);
        Assert.Equal([0xEE, (byte)Password.Length], details.Request.EncryptedInitialPassword);
        Assert.Equal(21, details.Request.Steps.Count);
        Assert.Equal("Vertrieb", details.Request.ConfigSnapshot!.Department.Name);
        Assert.DoesNotContain(details.Audit, a => (a.Details ?? "").Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Weak_password_is_rejected_without_echoing_it()
    {
        var (_, id) = await SubmittedAsync();
        _host.SignInAdmin("it.admin2");

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => _host.Requests.ApproveAsync(id, new SecretString("lirion12345!")));

        Assert.DoesNotContain("lirion12345!", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _host.Encryptor.Calls);
    }

    [Fact]
    public async Task Requester_cannot_approve_and_admin_cannot_approve_own_request()
    {
        var departmentId = await _host.CreateDepartmentAsync();
        _host.SignInHr();
        var hrRequest = await _host.Requests.CreateAsync(WebTestHost.Input(departmentId), submit: true);
        await Assert.ThrowsAsync<UserFacingException>(() => _host.Requests.ApproveAsync(hrRequest, new SecretString(Password)));

        _host.SignInAdmin();
        var adminRequest = await _host.Requests.CreateAsync(WebTestHost.Input(departmentId, "Lisa", "Berg"), submit: true);
        var ex = await Assert.ThrowsAsync<UserFacingException>(() => _host.Requests.ApproveAsync(adminRequest, new SecretString(Password)));
        Assert.Contains("Vier-Augen", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_needs_input_for_double_name()
    {
        var (_, id) = await SubmittedAsync("Anna-Lena", "Schmidt", mail: "a.schmidt@cleancontrolling.de", extension: null);
        _host.SignInAdmin();

        await _host.Requests.ResolveNeedsInputAsync(id, "aschmidt", "A.Schmidt-Berg@cleancontrolling.de");

        var request = (await _host.Requests.GetDetailsAsync(id))!.Request;
        Assert.Equal(RequestStatus.PendingApproval, request.Status);
        Assert.Equal("a.schmidt-berg@cleancontrolling.de", request.Derived!.Mail);
        Assert.Equal("smtp:a.schmidt-berg@cleancontrolling.com", request.Derived.ProxyAddresses[1]);
    }

    [Fact]
    public async Task Stale_write_reports_concurrency_conflict()
    {
        var (_, id) = await SubmittedAsync();
        _host.SignInAdmin("it.admin2");
        await _host.Requests.ApproveAsync(id, new SecretString(Password));

        // Simulate the worker changing the request between load and save.
        await using (var db = _host.DbFactory.CreateDbContext())
        {
            var request = db.Requests.Single(r => r.Id == id);
            db.Entry(request).Property(r => r.Version).CurrentValue = request.Version + 5;
            await db.SaveChangesAsync();
        }

        // A save based on a stale version must fail; emulate by cancelling twice in parallel contexts.
        await using var a = _host.DbFactory.CreateDbContext();
        await using var b = _host.DbFactory.CreateDbContext();
        var ra = a.Requests.Single(r => r.Id == id);
        var rb = b.Requests.Single(r => r.Id == id);
        new Onboarding.Core.Workflow.RequestWorkflow(_host.Time).Cancel(ra, Actor.Create("x", Role.ITAdmin), "a");
        await a.SaveChangesAsync();
        new Onboarding.Core.Workflow.RequestWorkflow(_host.Time).Cancel(rb, Actor.Create("y", Role.ITAdmin), "b");
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
    }

    [Fact]
    public async Task Overview_rows_progress_and_highlighting()
    {
        var (departmentId, id) = await SubmittedAsync();
        _host.SignInHr();
        var anna = await _host.Requests.CreateAsync(WebTestHost.Input(departmentId, "Anna-Lena", "Schmidt", "a.schmidt@cleancontrolling.de", null), submit: true);
        _host.SignInAdmin("it.admin2");
        await _host.Requests.ApproveAsync(id, new SecretString(Password));

        var rows = await _host.Requests.GetOverviewAsync(new OverviewFilter());

        var lenox = rows.Single(r => r.Id == id);
        Assert.Equal(21, lenox.StepsTotal);
        Assert.Equal(0, lenox.StepsDone); // nothing skipped: extension, voicemail and forwarding configured
        Assert.Equal("0/7", $"{lenox.ChecklistDone}/{lenox.ChecklistTotal}");
        Assert.Equal(2, lenox.DaysToDeadline);
        Assert.True(lenox.IsDueSoon);
        Assert.True(rows.Single(r => r.Id == anna).IsCritical);

        var onlyNeedsInput = await _host.Requests.GetOverviewAsync(new OverviewFilter(Status: RequestStatus.NeedsInput));
        Assert.Equal([anna], onlyNeedsInput.Select(r => r.Id));
    }

    [Fact]
    public async Task Requester_gets_summary_without_technical_details()
    {
        var (_, id) = await SubmittedAsync();
        _host.SignInAdmin("it.admin2");
        await _host.Requests.ApproveAsync(id, new SecretString(Password));
        _host.SignInHr();

        await Assert.ThrowsAsync<UserFacingException>(() => _host.Requests.GetDetailsAsync(id));
        var summary = (await _host.Requests.GetSummaryAsync(id))!;

        Assert.Equal(RequestStatus.Approved, summary.Status);
        Assert.Equal(21, summary.StepsTotal);
        Assert.Equal(2, summary.DaysToDeadline);
        Assert.Equal(7, summary.Checklist.Count);
        // The DTO type itself carries no steps, errors, audit or derived identity.
        Assert.DoesNotContain(typeof(RequestSummary).GetProperties(), p =>
            p.Name is "Steps" or "Audit" or "Derived" or "LastError" or "Request");
    }

    [Fact]
    public async Task Requester_may_load_own_draft_details_for_editing()
    {
        var departmentId = await _host.CreateDepartmentAsync();
        _host.SignInHr();
        var id = await _host.Requests.CreateAsync(WebTestHost.Input(departmentId), submit: false);

        Assert.NotNull(await _host.Requests.GetDetailsAsync(id));
    }

    [Fact]
    public async Task Past_entry_date_requires_confirmation_and_is_audited()
    {
        var departmentId = await _host.CreateDepartmentAsync();
        _host.SignInHr();
        var input = WebTestHost.Input(departmentId);
        input.EffectiveDate = new DateOnly(2026, 9, 28);

        Assert.True(await _host.Requests.IsInPastAsync(input.EffectiveDate));
        await Assert.ThrowsAsync<UserFacingException>(() => _host.Requests.CreateAsync(input, submit: true));

        var id = await _host.Requests.CreateAsync(input, submit: true, pastDateConfirmed: true);
        _host.SignInAdmin("it.admin2");
        await _host.Requests.ApproveAsync(id, new SecretString(Password));

        var details = (await _host.Requests.GetDetailsAsync(id))!;
        Assert.Contains(details.Audit, a => a.Action == AuditActions.PastEffectiveDateConfirmed && a.Actor == "hr.mueller");
        // AD.Enable is due immediately.
        Assert.True(details.Request.FindStep(AdEnable)!.NextAttemptAt <= _host.Time.GetUtcNow());
    }

    [Fact]
    public async Task Overview_hides_closed_unless_requested()
    {
        var (_, id) = await SubmittedAsync();
        await _host.Requests.CancelAsync(id, "doch nicht");

        Assert.Empty(await _host.Requests.GetOverviewAsync(new OverviewFilter()));
        Assert.Single(await _host.Requests.GetOverviewAsync(new OverviewFilter(IncludeClosed: true)));
    }

    [Fact]
    public async Task Requester_can_tick_hr_checklist_items_only()
    {
        var departmentId = await _host.CreateDepartmentAsync();
        var templates = await _host.Config.GetChecklistTemplatesAsync();
        await _host.Config.SaveChecklistTemplateAsync(new ChecklistTemplate
        {
            Id = Guid.NewGuid(), Title = "Arbeitsvertrag", Mandatory = true, Responsible = ChecklistResponsibility.HR, SortOrder = 100,
        });
        _host.SignInHr();
        var id = await _host.Requests.CreateAsync(WebTestHost.Input(departmentId), submit: true);
        _host.SignInAdmin("it.admin2");
        await _host.Requests.ApproveAsync(id, new SecretString(Password));

        // AD.CreateUser done (worker in phase 3)
        await using (var db = _host.DbFactory.CreateDbContext())
        {
            var step = db.RequestSteps.Single(s => s.RequestId == id && s.StepKey == AdCreateUser);
            step.TransitionTo(StepStatus.Running, _host.Time.GetUtcNow());
            step.TransitionTo(StepStatus.Done, _host.Time.GetUtcNow());
            await db.SaveChangesAsync();
        }

        var request = (await _host.Requests.GetDetailsAsync(id))!.Request;
        var hrItem = request.Checklist.Single(c => c.Responsible == ChecklistResponsibility.HR);
        var itItem = request.Checklist.First(c => c.Responsible == ChecklistResponsibility.IT);
        Assert.Equal(templates.Count + 1, request.Checklist.Count);

        _host.SignInHr();
        await _host.Requests.SetChecklistItemAsync(id, hrItem.Id, ChecklistItemStatus.Done, null);
        await Assert.ThrowsAsync<UserFacingException>(() => _host.Requests.SetChecklistItemAsync(id, itItem.Id, ChecklistItemStatus.Done, null));
    }
}
