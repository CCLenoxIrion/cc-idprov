using System.Text.Json;
using Onboarding.Core.Directory;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;

namespace Onboarding.Steps.Fakes;

/// <summary>
/// Sample directory content for development and tests, loaded from a JSON file. Nothing here
/// touches a real system (CLAUDE.md: fakes until phase 3).
/// </summary>
public sealed class FakeDirectoryData
{
    public List<FakeDirectoryObject> Objects { get; set; } = [];
    public List<OrganizationalUnit> OrganizationalUnits { get; set; } = [];
    public List<LicenseAvailability> Licenses { get; set; } = [];
    public PasswordPolicy PasswordPolicy { get; set; } = new(12, ComplexityEnabled: true);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static FakeDirectoryData Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<FakeDirectoryData>(stream, Options) ?? new FakeDirectoryData();
    }
}

public sealed class FakeDirectoryObject
{
    public Guid ObjectGuid { get; set; } = Guid.NewGuid();
    public DirectoryObjectClass ObjectClass { get; set; } = DirectoryObjectClass.User;
    public string DisplayName { get; set; } = "";
    public string? SamAccountName { get; set; }
    public string? Mail { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? TelephoneE164 { get; set; }
    public string? DistinguishedName { get; set; }
    public List<string> ProxyAddresses { get; set; } = [];
    public Guid? RequestId { get; set; }
}
