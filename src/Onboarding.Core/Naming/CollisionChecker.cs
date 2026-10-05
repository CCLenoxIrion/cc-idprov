using Onboarding.Core.Domain;

namespace Onboarding.Core.Naming;

public enum DirectoryObjectClass
{
    User,
    Group,
    Contact,
    SharedMailbox,
    Other,
}

/// <summary>An object found in AD/Entra/Exchange.</summary>
/// <param name="ObjectGuid">objectGUID (AD) or object id (Entra).</param>
/// <param name="ObjectClass">Kind of object.</param>
/// <param name="DisplayName">For messages.</param>
/// <param name="ProvisionedForRequestId">
/// Value of the configured request-id attribute (<c>GlobalConfig.RequestIdAttribute</c>), if it
/// holds a request id (DECISIONS K6).
/// </param>
public sealed record DirectoryObjectRef(
    Guid ObjectGuid,
    DirectoryObjectClass ObjectClass,
    string DisplayName,
    Guid? ProvisionedForRequestId = null);

/// <summary>
/// Read-only directory queries for collision checks. Implemented in phase 4 against AD/Entra;
/// fakes before. Address lookups are case-insensitive.
/// </summary>
public interface IDirectoryLookup
{
    Task<IReadOnlyList<DirectoryObjectRef>> FindBySamAccountNameAsync(string samAccountName, CancellationToken cancellationToken);

    /// <summary>Objects whose <c>mail</c> or <c>userPrincipalName</c> equals the address.</summary>
    Task<IReadOnlyList<DirectoryObjectRef>> FindByMailOrUpnAsync(string address, CancellationToken cancellationToken);

    /// <summary>
    /// Objects of <b>any</b> class (users, groups, contacts, shared mailboxes, …) that carry
    /// <c>smtp:{address}</c> in <c>proxyAddresses</c> (DECISIONS K2).
    /// </summary>
    Task<IReadOnlyList<DirectoryObjectRef>> FindProxyAddressOwnersAsync(string address, CancellationToken cancellationToken);

    /// <summary>Objects that already use the E.164 number.</summary>
    Task<IReadOnlyList<DirectoryObjectRef>> FindByPhoneNumberAsync(string e164, CancellationToken cancellationToken);
}

/// <summary>Identity values reserved by open (not closed) requests.</summary>
public sealed record OpenRequestIdentity(
    Guid RequestId,
    string SamAccountName,
    string Mail,
    string UserPrincipalName,
    IReadOnlyList<string> ProxyAddresses,
    string? PhoneE164);

public interface IOpenRequestLookup
{
    Task<IReadOnlyList<OpenRequestIdentity>> GetOpenRequestIdentitiesAsync(Guid excludeRequestId, CancellationToken cancellationToken);
}

public enum CollisionField
{
    SamAccountName,
    Mail,
    UserPrincipalName,
    ProxyAddress,
    PhoneNumber,
}

public enum CollisionSource
{
    Directory,
    OpenRequest,
}

public sealed record Collision(CollisionField Field, string Value, CollisionSource Source, string Description);

/// <summary>
/// Checks sam, mail, UPN, proxyAddresses and phone number against the directory and open
/// requests (DECISIONS K1–K3). Collisions lead to NeedsInput, never to numbering. The request's
/// own account (by stored objectGUID or request-id attribute) is not a collision (K6).
/// </summary>
public sealed class CollisionChecker(IDirectoryLookup directory, IOpenRequestLookup openRequests)
{
    public async Task<IReadOnlyList<Collision>> CheckAsync(
        Guid requestId,
        DerivedIdentity identity,
        Guid? ownDirectoryObjectGuid = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var collisions = new List<Collision>();
        bool Foreign(DirectoryObjectRef hit) =>
            hit.ObjectGuid != ownDirectoryObjectGuid && hit.ProvisionedForRequestId != requestId;

        foreach (var hit in (await directory.FindBySamAccountNameAsync(identity.SamAccountName, cancellationToken).ConfigureAwait(false)).Where(Foreign))
        {
            collisions.Add(Directory(CollisionField.SamAccountName, identity.SamAccountName, hit));
        }

        // Every address we would claim must be free as mail, UPN and proxy address of any object.
        var addresses = new List<(CollisionField Field, string Address)>
        {
            (CollisionField.Mail, identity.Mail),
            (CollisionField.UserPrincipalName, identity.UserPrincipalName),
        };
        addresses.AddRange(identity.ProxyAddresses
            .Select(SmtpAddress)
            .OfType<string>()
            .Select(a => (CollisionField.ProxyAddress, a)));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, address) in addresses)
        {
            if (!seen.Add(address))
            {
                continue;
            }

            var hits = (await directory.FindByMailOrUpnAsync(address, cancellationToken).ConfigureAwait(false))
                .Concat(await directory.FindProxyAddressOwnersAsync(address, cancellationToken).ConfigureAwait(false))
                .Where(Foreign)
                .DistinctBy(h => h.ObjectGuid);
            collisions.AddRange(hits.Select(hit => Directory(field, address, hit)));
        }

        if (identity.PhoneE164 is not null)
        {
            foreach (var hit in (await directory.FindByPhoneNumberAsync(identity.PhoneE164, cancellationToken).ConfigureAwait(false)).Where(Foreign))
            {
                collisions.Add(Directory(CollisionField.PhoneNumber, identity.PhoneE164, hit));
            }
        }

        var open = await openRequests.GetOpenRequestIdentitiesAsync(requestId, cancellationToken).ConfigureAwait(false);
        foreach (var other in open)
        {
            if (string.Equals(other.SamAccountName, identity.SamAccountName, StringComparison.OrdinalIgnoreCase))
            {
                collisions.Add(Open(CollisionField.SamAccountName, identity.SamAccountName, other));
            }

            var otherAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { other.Mail, other.UserPrincipalName };
            otherAddresses.UnionWith(other.ProxyAddresses.Select(SmtpAddress).OfType<string>());
            foreach (var address in seen.Where(otherAddresses.Contains))
            {
                collisions.Add(Open(FieldFor(address, identity), address, other));
            }

            if (identity.PhoneE164 is not null && string.Equals(other.PhoneE164, identity.PhoneE164, StringComparison.Ordinal))
            {
                collisions.Add(Open(CollisionField.PhoneNumber, identity.PhoneE164, other));
            }
        }

        return collisions;
    }

    /// <summary>Address part of an SMTP proxy address (either case of the prefix), else null.</summary>
    public static string? SmtpAddress(string proxyAddress)
    {
        ArgumentNullException.ThrowIfNull(proxyAddress);
        const string prefix = "smtp:";
        return proxyAddress.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? proxyAddress[prefix.Length..] : null;
    }

    private static CollisionField FieldFor(string address, DerivedIdentity identity) =>
        string.Equals(address, identity.Mail, StringComparison.OrdinalIgnoreCase) ? CollisionField.Mail
        : string.Equals(address, identity.UserPrincipalName, StringComparison.OrdinalIgnoreCase) ? CollisionField.UserPrincipalName
        : CollisionField.ProxyAddress;

    private static Collision Directory(CollisionField field, string value, DirectoryObjectRef hit) =>
        new(field, value, CollisionSource.Directory,
            $"{field} '{value}' ist bereits vergeben ({hit.ObjectClass}: {hit.DisplayName}).");

    private static Collision Open(CollisionField field, string value, OpenRequestIdentity other) =>
        new(field, value, CollisionSource.OpenRequest,
            $"{field} '{value}' ist bereits in offenem Auftrag {other.RequestId} reserviert.");
}
