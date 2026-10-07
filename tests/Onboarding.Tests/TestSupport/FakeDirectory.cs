using Onboarding.Core.Naming;

namespace Onboarding.Tests.TestSupport;

/// <summary>In-memory directory for collision tests. No real directory access.</summary>
internal sealed class FakeDirectory : IDirectoryLookup, IOpenRequestLookup
{
    public List<FakeObject> Objects { get; } = [];
    public List<OpenRequestIdentity> OpenRequests { get; } = [];

    public FakeObject Add(DirectoryObjectClass objectClass, string name, string? sam = null, string? mail = null,
        string? upn = null, string? phone = null, params string[] proxyAddresses) =>
        Add(objectClass, name, sam, null, mail, upn, phone, proxyAddresses);

    public FakeObject Add(DirectoryObjectClass objectClass, string name, string? sam, Guid? requestId,
        string? mail = null, string? upn = null, string? phone = null, params string[] proxyAddresses)
    {
        var obj = new FakeObject(Guid.NewGuid(), objectClass, name, sam, mail, upn, phone, proxyAddresses, requestId);
        Objects.Add(obj);
        return obj;
    }

    public Task<IReadOnlyList<DirectoryObjectRef>> FindBySamAccountNameAsync(string samAccountName, CancellationToken cancellationToken) =>
        Find(o => Eq(o.Sam, samAccountName));

    public Task<IReadOnlyList<DirectoryObjectRef>> FindByMailOrUpnAsync(string address, CancellationToken cancellationToken) =>
        Find(o => Eq(o.Mail, address) || Eq(o.Upn, address));

    public Task<IReadOnlyList<DirectoryObjectRef>> FindProxyAddressOwnersAsync(string address, CancellationToken cancellationToken) =>
        Find(o => o.ProxyAddresses.Any(p => Eq(p, "smtp:" + address)));

    public Task<IReadOnlyList<DirectoryObjectRef>> FindByPhoneNumberAsync(string e164, string? extension, CancellationToken cancellationToken) =>
        Find(o => o.Phone == e164);

    public Task<IReadOnlyList<OpenRequestIdentity>> GetOpenRequestIdentitiesAsync(Guid excludeRequestId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OpenRequestIdentity>>(OpenRequests.Where(r => r.RequestId != excludeRequestId).ToList());

    private Task<IReadOnlyList<DirectoryObjectRef>> Find(Func<FakeObject, bool> predicate) =>
        Task.FromResult<IReadOnlyList<DirectoryObjectRef>>(
            Objects.Where(predicate).Select(o => new DirectoryObjectRef(o.Id, o.ObjectClass, o.Name, o.RequestId)).ToList());

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal sealed record FakeObject(
        Guid Id,
        DirectoryObjectClass ObjectClass,
        string Name,
        string? Sam,
        string? Mail,
        string? Upn,
        string? Phone,
        string[] ProxyAddresses,
        Guid? RequestId);
}
