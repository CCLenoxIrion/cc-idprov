namespace Onboarding.Core.Domain;

/// <summary>Checklist template (SPEC §4.5).</summary>
public sealed class ChecklistTemplate : IVersioned
{
    public Guid Id { get; set; }
    public RequestType Type { get; set; }
    public ChecklistScope Scope { get; set; }

    /// <summary>Area id for <see cref="ChecklistScope.Area"/>, department id for <see cref="ChecklistScope.Department"/>, else null.</summary>
    public Guid? ScopeRefId { get; set; }

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Mandatory { get; set; }
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
    public long Version { get; set; }
}
