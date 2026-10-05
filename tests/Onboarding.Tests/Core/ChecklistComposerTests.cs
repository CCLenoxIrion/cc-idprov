using Onboarding.Core.Checklists;
using Onboarding.Core.Domain;
using Onboarding.Tests.TestSupport;

namespace Onboarding.Tests.Core;

public sealed class ChecklistComposerTests
{
    private static ChecklistTemplate T(
        string title,
        ChecklistScope scope = ChecklistScope.Global,
        Guid? scopeRef = null,
        int order = 0,
        bool active = true,
        RequestType type = RequestType.Onboarding,
        bool mandatory = true) => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Scope = scope,
            ScopeRefId = scopeRef,
            SortOrder = order,
            Active = active,
            Type = type,
            Mandatory = mandatory,
        };

    private static List<ChecklistItem> Compose(params ChecklistTemplate[] templates) =>
        ChecklistComposer.Compose(Guid.NewGuid(), RequestType.Onboarding, TestConfig.AreaId, TestConfig.DepartmentId, templates);

    [Fact]
    public void Merges_global_then_area_then_department_each_by_sort_order()
    {
        var items = Compose(
            T("Dept B", ChecklistScope.Department, TestConfig.DepartmentId, 2),
            T("Global 2", order: 20),
            T("Area A", ChecklistScope.Area, TestConfig.AreaId, 1),
            T("Global 1", order: 10),
            T("Dept A", ChecklistScope.Department, TestConfig.DepartmentId, 1));

        Assert.Equal(["Global 1", "Global 2", "Area A", "Dept A", "Dept B"], items.Select(i => i.Title));
        Assert.Equal([0, 1, 2, 3, 4], items.Select(i => i.SortOrder));
    }

    [Fact]
    public void Filters_scope_type_and_inactive()
    {
        var items = Compose(
            T("Other area", ChecklistScope.Area, TestConfig.OtherAreaId),
            T("Other dept", ChecklistScope.Department, TestConfig.OtherDepartmentId),
            T("Inactive", active: false),
            T("Offboarding", type: RequestType.Offboarding),
            T("Kept"));

        Assert.Equal(["Kept"], items.Select(i => i.Title));
    }

    [Fact]
    public void Items_are_snapshots_independent_of_templates()
    {
        var template = T("Original", mandatory: true);
        var items = Compose(template);

        template.Title = "Changed";
        template.Mandatory = false;
        template.Active = false;

        Assert.Equal("Original", items[0].Title);
        Assert.True(items[0].Mandatory);
        Assert.Equal(template.Id, items[0].SourceTemplateId);
        Assert.Equal(ChecklistItemStatus.Open, items[0].Status);
    }
}
