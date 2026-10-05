using Onboarding.Core.Directory;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;

namespace Onboarding.Steps.Fakes;

/// <summary>In-memory directory, license and password-policy fake.</summary>
public sealed class FakeDirectory(FakeDirectoryData data)
    : IDirectoryLookup, IDirectoryBrowser, IPasswordPolicyProvider, ILicenseOverview
{
    public Task<IReadOnlyList<DirectoryObjectRef>> FindBySamAccountNameAsync(string samAccountName, CancellationToken cancellationToken) =>
        Find(o => Eq(o.SamAccountName, samAccountName));

    public Task<IReadOnlyList<DirectoryObjectRef>> FindByMailOrUpnAsync(string address, CancellationToken cancellationToken) =>
        Find(o => Eq(o.Mail, address) || Eq(o.UserPrincipalName, address));

    public Task<IReadOnlyList<DirectoryObjectRef>> FindProxyAddressOwnersAsync(string address, CancellationToken cancellationToken) =>
        Find(o => o.ProxyAddresses.Any(p => Eq(CollisionChecker.SmtpAddress(p), address)));

    public Task<IReadOnlyList<DirectoryObjectRef>> FindByPhoneNumberAsync(string e164, CancellationToken cancellationToken) =>
        Find(o => string.Equals(o.TelephoneE164, e164, StringComparison.Ordinal));

    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var result = data.Objects
            .Where(o => o.ObjectClass == DirectoryObjectClass.User)
            .Where(o => string.IsNullOrWhiteSpace(query) ||
                        o.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (o.SamAccountName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(o => o.DisplayName, StringComparer.CurrentCulture)
            .Take(maxResults)
            .Select(ToUser)
            .ToList();
        return Task.FromResult<IReadOnlyList<DirectoryUser>>(result);
    }

    public Task<DirectoryUser?> GetUserAsync(Guid objectGuid, CancellationToken cancellationToken) =>
        Task.FromResult(data.Objects
            .Where(o => o.ObjectClass == DirectoryObjectClass.User && o.ObjectGuid == objectGuid)
            .Select(ToUser)
            .FirstOrDefault());

    public Task<IReadOnlyList<OrganizationalUnit>> GetOrganizationalUnitsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OrganizationalUnit>>(data.OrganizationalUnits.ToList());

    public Task<bool> OrganizationalUnitExistsAsync(string distinguishedName, CancellationToken cancellationToken) =>
        Task.FromResult(data.OrganizationalUnits.Any(ou => Eq(ou.DistinguishedName, distinguishedName)));

    Task<PasswordPolicy> IPasswordPolicyProvider.GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(data.PasswordPolicy);

    Task<IReadOnlyList<LicenseAvailability>> ILicenseOverview.GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LicenseAvailability>>(data.Licenses.ToList());

    private Task<IReadOnlyList<DirectoryObjectRef>> Find(Func<FakeDirectoryObject, bool> predicate) =>
        Task.FromResult<IReadOnlyList<DirectoryObjectRef>>(data.Objects
            .Where(predicate)
            .Select(o => new DirectoryObjectRef(o.ObjectGuid, o.ObjectClass, o.DisplayName, o.RequestId))
            .ToList());

    private static DirectoryUser ToUser(FakeDirectoryObject o) =>
        new(o.ObjectGuid, o.DisplayName, o.Mail, o.DistinguishedName);

    private static bool Eq(string? a, string? b) => a is not null && b is not null &&
                                                    string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
