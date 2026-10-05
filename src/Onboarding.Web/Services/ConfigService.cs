using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Configuration;
using Onboarding.Core.Directory;
using Onboarding.Core.Domain;
using Onboarding.Data;
using Onboarding.Data.Entities;

namespace Onboarding.Web.Services;

public sealed record LogonScriptRegeneration(Guid RequestId, string SamAccountName, string OldText, string NewText)
{
    public bool Changed => OldText != NewText;
}

/// <summary>
/// Reads and writes the configuration (SPEC §4). Writes need ITAdmin; every change is recorded
/// in the configuration history by the DbContext interceptor.
/// </summary>
public sealed class ConfigService(
    IDbContextFactory<OnboardingDbContext> dbFactory,
    CurrentUser currentUser,
    IDirectoryBrowser directory)
{
    public async Task<GlobalConfigRecord> GetGlobalAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.GlobalConfig.AsNoTracking().SingleAsync(ct);
    }

    public async Task SaveGlobalAsync(GlobalConfig settings, long expectedVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await RequireAdminAsync();
        ThrowIfInvalid(ConfigValidator.ValidateGlobal(settings));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.GlobalConfig.SingleAsync(ct);
        record.Settings = settings;
        await SaveAsync(db, record, expectedVersion, ct);
    }

    public async Task<List<AreaConfig>> GetAreasAsync(bool includeInactive = true, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Areas.AsNoTracking()
            .Where(a => includeInactive || a.Active)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    public async Task SaveAreaAsync(AreaConfig area, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        await RequireAdminAsync();
        ThrowIfInvalid(ConfigValidator.ValidateArea(area));
        if (!await directory.OrganizationalUnitExistsAsync(area.OuDistinguishedName, ct))
        {
            throw new UserFacingException($"OU '{area.OuDistinguishedName}' existiert nicht im Verzeichnis.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await UpsertAsync(db, db.Areas, area, ct);
    }

    public async Task<List<DepartmentConfig>> GetDepartmentsAsync(bool includeInactive = true, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Departments.AsNoTracking()
            .Where(d => includeInactive || d.Active)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
    }

    public async Task<DepartmentConfig?> GetDepartmentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task SaveDepartmentAsync(DepartmentConfig department, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(department);
        await RequireAdminAsync();
        department.Name = department.Name.Trim();
        ThrowIfInvalid(ConfigValidator.ValidateDepartment(department));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Departments.AnyAsync(d => d.Name == department.Name && d.Id != department.Id, ct))
        {
            throw new UserFacingException($"Abteilung '{department.Name}' existiert bereits.");
        }

        await UpsertAsync(db, db.Departments, department, ct);
    }

    /// <summary>
    /// Diff preview for "regenerate logon scripts of all users of this department" (SPEC §4.4):
    /// compares the script written for each request (from its config snapshot) with the script the
    /// edited template would produce. Writing to NETLOGON is a worker job (phase 3/4).
    /// </summary>
    public async Task<List<LogonScriptRegeneration>> PreviewLogonScriptRegenerationAsync(
        Guid departmentId,
        LogonScriptTemplate template,
        string departmentName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var requests = await db.Requests.AsNoTracking()
            .Where(r => r.Input.DepartmentId == departmentId && r.Status != RequestStatus.Cancelled)
            .ToListAsync(ct);

        var result = new List<LogonScriptRegeneration>();
        foreach (var request in requests)
        {
            if (request.Derived is null || request.ConfigSnapshot is null ||
                request.FindStep(Core.Steps.StepKeys.LogonScript)?.Status != StepStatus.Done)
            {
                continue;
            }

            var values = new Core.LogonScripts.LogonScriptValues(
                request.Derived.SamAccountName, request.Derived.HomeUnc, departmentName);
            var oldText = Render(request.ConfigSnapshot.Department.LogonScript,
                values with { Department = request.ConfigSnapshot.Department.Name });
            var newText = Render(template, values);
            result.Add(new LogonScriptRegeneration(request.Id, request.Derived.SamAccountName, oldText, newText));
        }

        return result;

        static string Render(LogonScriptTemplate t, Core.LogonScripts.LogonScriptValues v)
        {
            try
            {
                return Core.LogonScripts.LogonScriptGenerator.ToText(Core.LogonScripts.LogonScriptGenerator.Generate(t, v));
            }
            catch (Exception ex) when (ex is Core.LogonScripts.LogonScriptException or Core.Naming.TemplateException)
            {
                return $"FEHLER: {ex.Message}";
            }
        }
    }

    public async Task<List<ChecklistTemplate>> GetChecklistTemplatesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ChecklistTemplates.AsNoTracking()
            .OrderBy(t => t.Type).ThenBy(t => t.Scope).ThenBy(t => t.SortOrder)
            .ToListAsync(ct);
    }

    public async Task SaveChecklistTemplateAsync(ChecklistTemplate template, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        await RequireAdminAsync();
        if (string.IsNullOrWhiteSpace(template.Title))
        {
            throw new UserFacingException("Titel ist erforderlich.");
        }

        if (template.Scope == ChecklistScope.Global)
        {
            template.ScopeRefId = null;
        }
        else if (template.ScopeRefId is null)
        {
            throw new UserFacingException("Für Bereich/Abteilung muss ein Bezug gewählt werden.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await UpsertAsync(db, db.ChecklistTemplates, template, ct);
    }

    /// <summary>Swaps the sort order with the neighbour in the same type/scope/reference group.</summary>
    public async Task MoveChecklistTemplateAsync(Guid id, int direction, CancellationToken ct = default)
    {
        await RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var template = await db.ChecklistTemplates.SingleAsync(t => t.Id == id, ct);
        var group = await db.ChecklistTemplates
            .Where(t => t.Type == template.Type && t.Scope == template.Scope && t.ScopeRefId == template.ScopeRefId)
            .OrderBy(t => t.SortOrder)
            .ToListAsync(ct);
        var index = group.FindIndex(t => t.Id == id);
        var other = index + Math.Sign(direction);
        if (other < 0 || other >= group.Count)
        {
            return;
        }

        // Normalize to distinct values first, then swap.
        for (var i = 0; i < group.Count; i++)
        {
            group[i].SortOrder = (i + 1) * 10;
        }

        (group[index].SortOrder, group[other].SortOrder) = (group[other].SortOrder, group[index].SortOrder);
        await SaveAsync(db, ct);
    }

    public async Task<List<ConfigHistoryEntry>> GetHistoryAsync(string? entityType, int take, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ConfigHistory.AsNoTracking()
            .Where(h => entityType == null || h.EntityType == entityType)
            .OrderByDescending(h => h.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<IReadOnlyList<OrganizationalUnit>> GetOrganizationalUnitsAsync(CancellationToken ct = default) =>
        directory.GetOrganizationalUnitsAsync(ct);

    private async Task RequireAdminAsync()
    {
        var actor = await currentUser.GetAsync();
        if (!actor.IsITAdmin)
        {
            throw new UserFacingException("Konfiguration darf nur ein ITAdmin ändern.");
        }
    }

    private static void ThrowIfInvalid(IReadOnlyList<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new UserFacingException(string.Join(" ", errors));
        }
    }

    /// <summary>Inserts a new entity or copies the edited values onto the stored one (optimistic concurrency).</summary>
    private static async Task UpsertAsync<T>(OnboardingDbContext db, DbSet<T> set, T edited, CancellationToken ct)
        where T : class, IVersioned
    {
        var id = db.Entry(edited).Property("Id").CurrentValue!;
        var existing = await set.FindAsync([id], ct);
        if (existing is null)
        {
            edited.Version = 0;
            set.Add(edited);
            await SaveAsync(db, ct);
            return;
        }

        db.Entry(existing).CurrentValues.SetValues(edited);
        await SaveAsync(db, existing, edited.Version, ct);
    }

    private static async Task SaveAsync<T>(OnboardingDbContext db, T entity, long expectedVersion, CancellationToken ct)
        where T : class, IVersioned
    {
        var entry = db.Entry(entity);
        entry.Property(e => e.Version).OriginalValue = expectedVersion;
        entry.Property(e => e.Version).CurrentValue = expectedVersion;
        await SaveAsync(db, ct);
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
