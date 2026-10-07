using Onboarding.Core.Domain;

namespace Onboarding.Core.Configuration;

/// <summary>Area / company ("Bereich", SPEC §4.2). Determines OU and <c>company</c>.</summary>
public sealed class AreaConfig : IVersioned
{
    public Guid Id { get; set; }

    /// <summary>Short display name shown as radio button, e.g. "TecSa".</summary>
    public string Name { get; set; } = "";

    public string OuCanonical { get; set; } = "";
    public string OuDistinguishedName { get; set; } = "";
    public string Company { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
    public long Version { get; set; }
}
