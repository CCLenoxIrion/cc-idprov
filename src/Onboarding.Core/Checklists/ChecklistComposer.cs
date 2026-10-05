using Onboarding.Core.Domain;

namespace Onboarding.Core.Checklists;

/// <summary>
/// Merges all applicable checklist templates into the request's checklist (SPEC §4.5,
/// DECISIONS S4). Called when the request is created; items are copies.
/// </summary>
public static class ChecklistComposer
{
    public static List<ChecklistItem> Compose(
        Guid requestId,
        RequestType type,
        Guid areaId,
        Guid departmentId,
        IEnumerable<ChecklistTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var order = 0;
        return templates
            .Where(t => t.Active && t.Type == type && Applies(t, areaId, departmentId))
            .OrderBy(t => t.Scope)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.Title, StringComparer.Ordinal)
            .Select(t => new ChecklistItem
            {
                Id = Guid.NewGuid(),
                RequestId = requestId,
                SourceTemplateId = t.Id,
                Title = t.Title,
                Description = t.Description,
                Mandatory = t.Mandatory,
                Responsible = t.Responsible,
                SortOrder = order++,
            })
            .ToList();
    }

    private static bool Applies(ChecklistTemplate template, Guid areaId, Guid departmentId) => template.Scope switch
    {
        ChecklistScope.Global => true,
        ChecklistScope.Area => template.ScopeRefId == areaId,
        ChecklistScope.Department => template.ScopeRefId == departmentId,
        _ => false,
    };
}
