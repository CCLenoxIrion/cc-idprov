using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Configuration;
using Onboarding.Core.Directory;
using Onboarding.Core.Domain;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;
using Onboarding.Core.State;
using Onboarding.Core.Time;
using Onboarding.Core.Workflow;
using Onboarding.Data;

namespace Onboarding.Web.Services;

public sealed record FormContext(GlobalConfig Global, List<AreaConfig> Areas, List<DepartmentConfig> Departments);

public sealed record LicenseRow(string SkuPartNumber, int? Free);

public sealed record FormPreview(
    DerivedIdentity? Identity,
    IReadOnlyList<IdentityIssue> Issues,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<LicenseRow> Licenses);

public sealed record OverviewFilter(
    RequestStatus? Status = null,
    RequestType? Type = null,
    Guid? AreaId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    bool IncludeClosed = false);

public sealed record OverviewRow(
    Guid Id,
    string Name,
    RequestType Type,
    string Area,
    string Department,
    DateOnly Date,
    RequestStatus Status,
    int StepsDone,
    int StepsTotal,
    int ChecklistDone,
    int ChecklistTotal,
    int DaysToDeadline)
{
    /// <summary>SPEC §6.1: Failed/NeedsInput red.</summary>
    public bool IsCritical => Status is RequestStatus.Failed or RequestStatus.NeedsInput;

    /// <summary>SPEC §6.1: deadline within 3 days and not completed → orange.</summary>
    public bool IsDueSoon => !IsCritical && DaysToDeadline <= 3 &&
                             Status is not (RequestStatus.Completed or RequestStatus.Cancelled);
}

/// <summary>
/// Restricted view for requesters (DECISIONS W9): status, progress, deadline, checklist – no step
/// errors, no audit log, no technical values.
/// </summary>
public sealed record RequestSummary(
    Guid Id,
    string Name,
    RequestType Type,
    RequestStatus Status,
    string AreaName,
    string DepartmentName,
    DateOnly EffectiveDate,
    int DaysToDeadline,
    int StepsDone,
    int StepsTotal,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    bool ChecklistEditable,
    IReadOnlyList<ChecklistItem> Checklist);

public sealed record RequestDetails(
    Request Request,
    string AreaName,
    string DepartmentName,
    DirectoryUser? Manager,
    IReadOnlyList<AuditEntry> Audit);

/// <summary>
/// Request use cases for the UI. Loads data, gathers I/O-bound inputs (config snapshot, identity
/// check, encrypted password) and delegates the decision to <see cref="RequestWorkflow"/>.
/// Audit entries are saved in the same transaction as the request.
/// </summary>
public sealed class RequestService(
    IDbContextFactory<OnboardingDbContext> dbFactory,
    CurrentUser currentUser,
    RequestWorkflow workflow,
    IDirectoryLookup directoryLookup,
    IDirectoryBrowser directoryBrowser,
    IPasswordPolicyProvider passwordPolicyProvider,
    ILicenseOverview licenseOverview,
    ISecretEncryptor encryptor,
    TimeProvider timeProvider)
{
    public async Task<FormContext> GetFormContextAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var global = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings;
        var areas = await db.Areas.AsNoTracking().Where(a => a.Active).OrderBy(a => a.SortOrder).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.Active).OrderBy(d => d.Name).ToListAsync(ct);
        return new FormContext(global, areas, departments);
    }

    /// <summary>Live preview for the form: derived values, issues, collisions, warnings (DECISIONS K3).</summary>
    public async Task<FormPreview> PreviewAsync(Guid? requestId, PersonInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var global = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings;
        var area = await db.Areas.AsNoTracking().SingleOrDefaultAsync(a => a.Id == input.AreaId, ct);
        var department = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == input.DepartmentId, ct);
        if (area is null || department is null)
        {
            return new FormPreview(null, [new IdentityIssue(IdentityIssueCode.MissingName, "Bereich und Abteilung wählen.")], [], []);
        }

        var check = await CheckIdentityAsync(requestId ?? Guid.Empty, input, null, null, global, area, department, ct);
        var warnings = new List<string>();
        if (InputWarnings.Phone(department, input.Extension) is { } phoneWarning)
        {
            warnings.Add(phoneWarning);
        }

        var licenses = await LicenseRowsAsync(global, department, input.Extension, ct);
        warnings.AddRange(licenses.Where(l => l.Free is <= 0)
            .Select(l => $"Keine freien Lizenzen für {l.SkuPartNumber}."));
        return new FormPreview(check.Identity, check.Issues, warnings, licenses);
    }

    /// <summary>True if the entry date lies before today in the configured time zone (DECISIONS W10).</summary>
    public async Task<bool> IsInPastAsync(DateOnly date, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var global = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings;
        return date < BusinessCalendar.Today(timeProvider.GetUtcNow(), global.TimeZone);
    }

    public async Task<Guid> CreateAsync(PersonInput input, bool submit, bool pastDateConfirmed = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var actor = await currentUser.GetAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pastDate = await RequirePastDateConfirmationAsync(db, input.EffectiveDate, pastDateConfirmed, ct);
        var templates = await db.ChecklistTemplates.AsNoTracking().ToListAsync(ct);
        var (request, audit) = Run(() => workflow.Create(RequestType.Onboarding, input, actor, templates));
        var audits = new List<AuditEntry> { audit };
        if (pastDate)
        {
            audits.Add(PastDateAudit(actor, request.Id, input.EffectiveDate));
        }

        if (submit)
        {
            audits.AddRange(await SubmitInternalAsync(db, request, actor, ct));
        }

        db.Requests.Add(request);
        db.AuditLog.AddRange(audits);
        await SaveAsync(db, ct);
        return request.Id;
    }

    public Task UpdateDraftAsync(Guid id, PersonInput input, bool submit, bool pastDateConfirmed = false, CancellationToken ct = default) =>
        MutateAsync(id, async (db, request, actor) =>
        {
            var pastDate = await RequirePastDateConfirmationAsync(db, input.EffectiveDate, pastDateConfirmed, ct);
            var templates = await db.ChecklistTemplates.AsNoTracking().ToListAsync(ct);
            var audits = new List<AuditEntry>(workflow.UpdateDraft(request, actor, input, templates));
            if (pastDate)
            {
                audits.Add(PastDateAudit(actor, request.Id, input.EffectiveDate));
            }

            if (submit)
            {
                audits.AddRange(await SubmitInternalAsync(db, request, actor, ct));
            }

            return audits;
        }, ct);

    public Task SubmitAsync(Guid id, CancellationToken ct = default) =>
        MutateAsync(id, (db, request, actor) => SubmitInternalAsync(db, request, actor, ct), ct);

    public async Task<PasswordPolicy> GetPasswordPolicyAsync(CancellationToken ct = default) =>
        await passwordPolicyProvider.GetAsync(ct);

    public async Task<SecretString> GeneratePasswordAsync(CancellationToken ct = default) =>
        PasswordPolicyValidator.Generate(await passwordPolicyProvider.GetAsync(ct));

    /// <summary>Checks the password against the domain policy; returns error texts (never the password).</summary>
    public async Task<IReadOnlyList<string>> ValidatePasswordAsync(Guid id, SecretString password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.Id == id, ct);
        var policy = await passwordPolicyProvider.GetAsync(ct);
        return PasswordPolicyValidator.Validate(password, policy, request.Derived?.SamAccountName, request.Derived?.DisplayName);
    }

    /// <summary>
    /// Approval (ITAdmin, four-eyes): validates the initial password, freezes the configuration,
    /// re-checks identity and collisions, encrypts the password for the worker (SPEC §2, §9).
    /// </summary>
    public Task ApproveAsync(Guid id, SecretString initialPassword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(initialPassword);
        return MutateAsync(id, async (db, request, actor) =>
        {
            var policy = await passwordPolicyProvider.GetAsync(ct);
            var errors = PasswordPolicyValidator.Validate(
                initialPassword, policy, request.Derived?.SamAccountName, request.Derived?.DisplayName);
            if (errors.Count > 0)
            {
                throw new UserFacingException("Startpasswort: " + string.Join(" ", errors));
            }

            var snapshot = await CurrentSnapshotAsync(db, request.Input, ct);
            var check = await CheckIdentityAsync(request, snapshot, ct);
            var ciphertext = encryptor.Encrypt(initialPassword);
            return workflow.Approve(request, actor, snapshot, check, ciphertext);
        }, ct);
    }

    /// <summary>ITAdmin assigns sam/mail manually to resolve NeedsInput (DECISIONS S1).</summary>
    public Task<IReadOnlyList<AuditEntry>> ResolveNeedsInputAsync(Guid id, string sam, string mail, CancellationToken ct = default) =>
        MutateAsync(id, async (db, request, actor) =>
        {
            var identityOverride = new IdentityOverride(sam.Trim().ToLowerInvariant(), mail.Trim().ToLowerInvariant());
            var snapshot = request.ConfigSnapshot ?? await CurrentSnapshotAsync(db, request.Input, ct);
            var check = await CheckIdentityAsync(
                request.Id, request.Input, identityOverride, request.DirectoryObjectGuid,
                snapshot.Global, snapshot.Area, snapshot.Department, ct);
            return workflow.ResolveNeedsInput(request, actor, identityOverride, check);
        }, ct);

    /// <summary>ITAdmin: replaces the configuration snapshot with the current configuration (DECISIONS S3).</summary>
    public Task<IReadOnlyList<AuditEntry>> RefreshSnapshotAsync(Guid id, CancellationToken ct = default) =>
        MutateAsync(id, async (db, request, actor) =>
        {
            var snapshot = await CurrentSnapshotAsync(db, request.Input, ct);
            var check = await CheckIdentityAsync(request, snapshot, ct);
            return workflow.RefreshSnapshot(request, actor, snapshot, check);
        }, ct);

    public Task CancelAsync(Guid id, string reason, CancellationToken ct = default) =>
        MutateAsync(id, (_, request, actor) => Task.FromResult(workflow.Cancel(request, actor, reason)), ct);

    public Task RetryStepAsync(Guid id, string stepKey, CancellationToken ct = default) =>
        MutateAsync(id, (_, request, actor) => Task.FromResult(workflow.RetryStep(request, stepKey, actor)), ct);

    public Task MarkStepDoneAsync(Guid id, string stepKey, string reason, CancellationToken ct = default) =>
        MutateAsync(id, (_, request, actor) => Task.FromResult(workflow.MarkStepDone(request, stepKey, actor, reason)), ct);

    public Task SetChecklistItemAsync(Guid id, Guid itemId, ChecklistItemStatus status, string? note, CancellationToken ct = default) =>
        MutateAsync(id, (_, request, actor) =>
            Task.FromResult(workflow.SetChecklistItemStatus(request, itemId, status, note, actor)), ct);

    /// <summary>
    /// Full details incl. steps, errors and audit log: ITAdmin only; requesters get their own
    /// drafts (for editing). Everything else uses <see cref="GetSummaryAsync"/> (DECISIONS W9).
    /// </summary>
    public async Task<RequestDetails?> GetDetailsAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await currentUser.GetAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.Requests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return null;
        }

        if (!actor.IsITAdmin && !(request.Status == RequestStatus.Draft && request.CreatedBy == actor.Name))
        {
            throw new UserFacingException("Die vollständige Detailansicht ist ITAdmins vorbehalten.");
        }

        var area = await db.Areas.AsNoTracking().Where(a => a.Id == request.Input.AreaId).Select(a => a.Name).SingleOrDefaultAsync(ct);
        var department = await db.Departments.AsNoTracking().Where(d => d.Id == request.Input.DepartmentId).Select(d => d.Name).SingleOrDefaultAsync(ct);
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.RequestId == id).OrderBy(a => a.Id).ToListAsync(ct);
        var manager = request.Input.ManagerObjectGuid == Guid.Empty
            ? null
            : await directoryBrowser.GetUserAsync(request.Input.ManagerObjectGuid, ct);
        return new RequestDetails(request, area ?? "?", department ?? "?", manager, audit);
    }

    public async Task<RequestSummary?> GetSummaryAsync(Guid id, CancellationToken ct = default)
    {
        var actor = await currentUser.GetAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.Requests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return null;
        }

        var global = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings;
        var area = await db.Areas.AsNoTracking().Where(a => a.Id == request.Input.AreaId).Select(a => a.Name).SingleOrDefaultAsync(ct);
        var department = await db.Departments.AsNoTracking().Where(d => d.Id == request.Input.DepartmentId).Select(d => d.Name).SingleOrDefaultAsync(ct);
        var today = BusinessCalendar.Today(timeProvider.GetUtcNow(), global.TimeZone);
        return new RequestSummary(
            request.Id,
            $"{request.Input.FirstName} {request.Input.LastName}",
            request.Type,
            request.Status,
            area ?? "?",
            department ?? "?",
            request.Input.EffectiveDate,
            request.Input.EffectiveDate.DayNumber - today.DayNumber,
            request.Steps.Count(s => StepStateMachine.SatisfiesDependency(s.Status)),
            request.Steps.Count,
            request.CreatedBy,
            request.CreatedAt,
            !request.IsClosed && RequestWorkflow.CanEditChecklist(request),
            request.Checklist.OrderBy(c => c.SortOrder).ToList());
    }

    public Task ForceStepAsync(Guid id, string stepKey, string reason, CancellationToken ct = default) =>
        MutateAsync(id, (_, request, actor) => Task.FromResult(workflow.ForceStep(request, stepKey, actor, reason)), ct);

    public async Task<IReadOnlyList<OverviewRow>> GetOverviewAsync(OverviewFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var global = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings;
        var areas = await db.Areas.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var departments = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var query = db.Requests.AsNoTracking();
        if (!filter.IncludeClosed)
        {
            query = query.Where(r => r.Status != RequestStatus.Completed && r.Status != RequestStatus.Cancelled);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(r => r.Status == status);
        }

        if (filter.Type is { } type)
        {
            query = query.Where(r => r.Type == type);
        }

        if (filter.AreaId is { } areaId)
        {
            query = query.Where(r => r.Input.AreaId == areaId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(r => r.Input.EffectiveDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => r.Input.EffectiveDate <= to);
        }

        var today = BusinessCalendar.Today(timeProvider.GetUtcNow(), global.TimeZone);
        var requests = await query.ToListAsync(ct);
        return requests
            .Select(r => new OverviewRow(
                r.Id,
                $"{r.Input.FirstName} {r.Input.LastName}",
                r.Type,
                areas.GetValueOrDefault(r.Input.AreaId, "?"),
                departments.GetValueOrDefault(r.Input.DepartmentId, "?"),
                r.Input.EffectiveDate,
                r.Status,
                r.Steps.Count(s => StepStateMachine.SatisfiesDependency(s.Status)),
                r.Steps.Count,
                r.Checklist.Count(c => c.IsSatisfied),
                r.Checklist.Count,
                r.Input.EffectiveDate.DayNumber - today.DayNumber))
            .OrderBy(r => r.Date)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(Guid? requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditLog.AsNoTracking()
            .Where(a => requestId == null || a.RequestId == requestId)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);
    }

    public Task<IReadOnlyList<DirectoryUser>> SearchManagersAsync(string query, CancellationToken ct = default) =>
        directoryBrowser.SearchUsersAsync(query, 15, ct);

    public Task<DirectoryUser?> GetManagerAsync(Guid objectGuid, CancellationToken ct = default) =>
        directoryBrowser.GetUserAsync(objectGuid, ct);

    private async Task<bool> RequirePastDateConfirmationAsync(OnboardingDbContext db, DateOnly date, bool confirmed, CancellationToken ct)
    {
        var global = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings;
        var inPast = date < BusinessCalendar.Today(timeProvider.GetUtcNow(), global.TimeZone);
        if (inPast && !confirmed)
        {
            throw new UserFacingException("Das Eintrittsdatum liegt in der Vergangenheit. Bitte ausdrücklich bestätigen.");
        }

        return inPast;
    }

    private AuditEntry PastDateAudit(Actor actor, Guid requestId, DateOnly date) =>
        AuditEntry.Create(timeProvider.GetUtcNow(), actor.Name, AuditActions.PastEffectiveDateConfirmed, AuditResults.Success,
            requestId, details: $"Eintrittsdatum {date:dd.MM.yyyy} liegt in der Vergangenheit (Nachmeldung); AD.Enable ist sofort fällig.");

    private async Task<IReadOnlyList<AuditEntry>> SubmitInternalAsync(OnboardingDbContext db, Request request, Actor actor, CancellationToken ct)
    {
        var snapshot = await CurrentSnapshotAsync(db, request.Input, ct);
        var check = await CheckIdentityAsync(request, snapshot, ct);
        return Run(() => workflow.Submit(request, actor, check));
    }

    private async Task<RequestConfigSnapshot> CurrentSnapshotAsync(OnboardingDbContext db, PersonInput input, CancellationToken ct)
    {
        var global = (await db.GlobalConfig.AsNoTracking().SingleAsync(ct)).Settings;
        var area = await db.Areas.AsNoTracking().SingleOrDefaultAsync(a => a.Id == input.AreaId, ct)
                   ?? throw new UserFacingException("Bereich existiert nicht.");
        var department = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == input.DepartmentId, ct)
                         ?? throw new UserFacingException("Abteilung existiert nicht.");
        return new RequestConfigSnapshot { Global = global, Area = area, Department = department, TakenAt = timeProvider.GetUtcNow() };
    }

    private Task<IdentityCheck> CheckIdentityAsync(Request request, RequestConfigSnapshot snapshot, CancellationToken ct) =>
        CheckIdentityAsync(request.Id, request.Input, request.IdentityOverride, request.DirectoryObjectGuid,
            snapshot.Global, snapshot.Area, snapshot.Department, ct);

    private async Task<IdentityCheck> CheckIdentityAsync(
        Guid requestId,
        PersonInput input,
        IdentityOverride? identityOverride,
        Guid? ownDirectoryObjectGuid,
        GlobalConfig global,
        AreaConfig area,
        DepartmentConfig department,
        CancellationToken ct)
    {
        DerivationResult derivation;
        try
        {
            derivation = IdentityDeriver.Derive(input, identityOverride, global, area, department);
        }
        catch (TemplateException ex)
        {
            throw new UserFacingException($"Konfigurationsfehler: {ex.Message}", ex);
        }

        if (derivation.Identity is null)
        {
            return IdentityCheck.From(derivation, []);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var checker = new CollisionChecker(directoryLookup, new OpenRequestLookup(db));
        var collisions = await checker.CheckAsync(requestId, derivation.Identity, ownDirectoryObjectGuid, ct);
        return IdentityCheck.From(derivation, collisions);
    }

    private async Task<IReadOnlyList<LicenseRow>> LicenseRowsAsync(GlobalConfig global, DepartmentConfig department, string? extension, CancellationToken ct)
    {
        if (global.LicenseMode != LicenseMode.Direct)
        {
            return [];
        }

        var skus = department.Licenses.SkuPartNumbers.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(extension))
        {
            skus = skus.Concat(department.Licenses.SkuPartNumbersIfPhone);
        }

        var available = await licenseOverview.GetAsync(ct);
        return skus.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(sku => new LicenseRow(sku, available.FirstOrDefault(a => string.Equals(a.SkuPartNumber, sku, StringComparison.OrdinalIgnoreCase))?.Free))
            .ToList();
    }

    private async Task<IReadOnlyList<AuditEntry>> MutateAsync(
        Guid id,
        Func<OnboardingDbContext, Request, Actor, Task<IReadOnlyList<AuditEntry>>> action,
        CancellationToken ct)
    {
        var actor = await currentUser.GetAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var request = await db.Requests.SingleOrDefaultAsync(r => r.Id == id, ct)
                      ?? throw new UserFacingException("Auftrag nicht gefunden.");
        IReadOnlyList<AuditEntry> audits;
        try
        {
            audits = await action(db, request, actor);
        }
        catch (WorkflowException ex)
        {
            throw new UserFacingException(ex.Message, ex);
        }
        catch (InvalidStateTransitionException ex)
        {
            throw new UserFacingException($"Aktion im aktuellen Status nicht möglich ({request.Status}).", ex);
        }

        db.AuditLog.AddRange(audits);
        await SaveAsync(db, ct);
        return audits;
    }

    private static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (WorkflowException ex)
        {
            throw new UserFacingException(ex.Message, ex);
        }
    }

    private static async Task SaveAsync(OnboardingDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new UserFacingException(UserFacingException.ConcurrencyMessage, ex);
        }
    }
}
