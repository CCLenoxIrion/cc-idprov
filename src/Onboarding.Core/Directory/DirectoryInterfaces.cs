using Onboarding.Core.Security;

namespace Onboarding.Core.Directory;

/// <summary>A user that can be selected as manager.</summary>
public sealed record DirectoryUser(Guid ObjectGuid, string DisplayName, string? Mail, string? DistinguishedName);

/// <summary>An organizational unit for the OU picker (SPEC §4.2).</summary>
public sealed record OrganizationalUnit(string DistinguishedName, string CanonicalName);

/// <summary>
/// Read-only directory browsing for the UI (manager selection, OU picker). Implemented against
/// AD in phase 4, fake before.
/// </summary>
public interface IDirectoryBrowser
{
    Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, int maxResults, CancellationToken cancellationToken);

    /// <summary>Resolves a user by objectGUID (DN only for display, DECISIONS D9).</summary>
    Task<DirectoryUser?> GetUserAsync(Guid objectGuid, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrganizationalUnit>> GetOrganizationalUnitsAsync(CancellationToken cancellationToken);

    Task<bool> OrganizationalUnitExistsAsync(string distinguishedName, CancellationToken cancellationToken);
}

/// <summary>Domain password policy (<c>Get-ADDefaultDomainPasswordPolicy</c>).</summary>
public interface IPasswordPolicyProvider
{
    Task<PasswordPolicy> GetAsync(CancellationToken cancellationToken);
}

/// <summary>License counts of one subscribed SKU (Graph <c>subscribedSkus</c>).</summary>
public sealed record LicenseAvailability(string SkuPartNumber, int Enabled, int Consumed)
{
    /// <summary>Free = PrepaidUnits.Enabled − ConsumedUnits (SPEC §11).</summary>
    public int Free => Enabled - Consumed;
}

public interface ILicenseOverview
{
    Task<IReadOnlyList<LicenseAvailability>> GetAsync(CancellationToken cancellationToken);
}
