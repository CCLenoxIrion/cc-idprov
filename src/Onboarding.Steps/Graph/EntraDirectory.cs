using System.Text.Json;
using Onboarding.Core.Directory;
using Onboarding.Core.Naming;

namespace Onboarding.Steps.Graph;

/// <summary>OData filter values (DECISIONS X11): quotes doubled; the whole filter is URL-encoded.</summary>
public static class OData
{
    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary><c>{collection}?$filter=…&amp;$select=…[&amp;$count=true]</c> with encoded filter.</summary>
    public static string Query(string collection, string filter, string select, bool count) =>
        $"{collection}?$filter={Uri.EscapeDataString(filter)}&$select={select}" + (count ? "&$count=true" : "");
}

/// <summary>Free licenses per SKU from <c>subscribedSkus</c> (SPEC §11: Enabled − Consumed).</summary>
public sealed class GraphLicenseOverview(GraphReadClient client) : ILicenseOverview
{
    public async Task<IReadOnlyList<LicenseAvailability>> GetAsync(CancellationToken cancellationToken)
    {
        var skus = await client.GetListAsync("subscribedSkus?$select=skuPartNumber,prepaidUnits,consumedUnits", false, cancellationToken)
            .ConfigureAwait(false);
        return skus
            .Select(s => new LicenseAvailability(
                s.GetProperty("skuPartNumber").GetString() ?? "",
                s.TryGetProperty("prepaidUnits", out var units) && units.TryGetProperty("enabled", out var enabled) ? enabled.GetInt32() : 0,
                s.TryGetProperty("consumedUnits", out var consumed) ? consumed.GetInt32() : 0))
            .OrderBy(l => l.SkuPartNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Entra lookups for the collision check (DECISIONS X16). Only <b>cloud-only</b> objects are
/// returned: synced objects (<c>onPremisesSyncEnabled = true</c>) are already found in AD, so
/// there is no dependence on the Entra Connect source anchor. sAMAccountName and phone numbers
/// are AD concepts and stay AD-only.
/// </summary>
public sealed class EntraDirectoryLookup(GraphReadClient client) : IDirectoryLookup
{
    private const string Select = "id,displayName,onPremisesSyncEnabled";

    public Task<IReadOnlyList<DirectoryObjectRef>> FindBySamAccountNameAsync(string samAccountName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DirectoryObjectRef>>([]);

    public Task<IReadOnlyList<DirectoryObjectRef>> FindByPhoneNumberAsync(string e164, string? extension, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DirectoryObjectRef>>([]);

    public async Task<IReadOnlyList<DirectoryObjectRef>> FindByMailOrUpnAsync(string address, CancellationToken cancellationToken)
    {
        var literal = OData.Literal(address);
        var hits = new List<DirectoryObjectRef>();
        // "or" across two properties is sent as advanced query (ConsistencyLevel + $count) to be safe.
        hits.AddRange(await FindAsync("users", $"userPrincipalName eq {literal} or mail eq {literal}", true, DirectoryObjectClass.User, cancellationToken).ConfigureAwait(false));
        hits.AddRange(await FindAsync("groups", $"mail eq {literal}", false, DirectoryObjectClass.Group, cancellationToken).ConfigureAwait(false));
        hits.AddRange(await FindAsync("contacts", $"mail eq {literal}", false, DirectoryObjectClass.Contact, cancellationToken).ConfigureAwait(false));
        return hits.DistinctBy(h => h.ObjectGuid).ToList();
    }

    /// <summary>
    /// Both prefixes are queried: whether Graph compares proxyAddresses case-insensitively is not
    /// verified. Contacts: proxyAddresses filter support is not verified – contacts are matched
    /// by mail only (documented limitation, X16).
    /// </summary>
    public async Task<IReadOnlyList<DirectoryObjectRef>> FindProxyAddressOwnersAsync(string address, CancellationToken cancellationToken)
    {
        var filter = $"proxyAddresses/any(p:p eq {OData.Literal("SMTP:" + address)}) or proxyAddresses/any(p:p eq {OData.Literal("smtp:" + address)})";
        var hits = new List<DirectoryObjectRef>();
        hits.AddRange(await FindAsync("users", filter, true, DirectoryObjectClass.User, cancellationToken).ConfigureAwait(false));
        hits.AddRange(await FindAsync("groups", filter, true, DirectoryObjectClass.Group, cancellationToken).ConfigureAwait(false));
        hits.AddRange(await FindAsync("contacts", $"mail eq {OData.Literal(address)}", false, DirectoryObjectClass.Contact, cancellationToken).ConfigureAwait(false));
        return hits.DistinctBy(h => h.ObjectGuid).ToList();
    }

    private async Task<IEnumerable<DirectoryObjectRef>> FindAsync(
        string collection, string filter, bool advanced, DirectoryObjectClass objectClass, CancellationToken cancellationToken)
    {
        var items = await client.GetListAsync(OData.Query(collection, filter, Select, advanced), advanced, cancellationToken).ConfigureAwait(false);
        return items
            .Where(i => !(i.TryGetProperty("onPremisesSyncEnabled", out var synced) && synced.ValueKind == JsonValueKind.True))
            .Select(i => new DirectoryObjectRef(
                Guid.TryParse(i.GetProperty("id").GetString(), out var id) ? id : Guid.Empty,
                objectClass,
                (i.TryGetProperty("displayName", out var name) ? name.GetString() : null) ?? "(Entra)"));
    }
}

/// <summary>AD and Entra hits together (DECISIONS X16); duplicates by object id removed.</summary>
public sealed class CompositeDirectoryLookup(IDirectoryLookup primary, IDirectoryLookup secondary) : IDirectoryLookup
{
    public Task<IReadOnlyList<DirectoryObjectRef>> FindBySamAccountNameAsync(string samAccountName, CancellationToken cancellationToken) =>
        Both(l => l.FindBySamAccountNameAsync(samAccountName, cancellationToken));

    public Task<IReadOnlyList<DirectoryObjectRef>> FindByMailOrUpnAsync(string address, CancellationToken cancellationToken) =>
        Both(l => l.FindByMailOrUpnAsync(address, cancellationToken));

    public Task<IReadOnlyList<DirectoryObjectRef>> FindProxyAddressOwnersAsync(string address, CancellationToken cancellationToken) =>
        Both(l => l.FindProxyAddressOwnersAsync(address, cancellationToken));

    public Task<IReadOnlyList<DirectoryObjectRef>> FindByPhoneNumberAsync(string e164, string? extension, CancellationToken cancellationToken) =>
        Both(l => l.FindByPhoneNumberAsync(e164, extension, cancellationToken));

    private async Task<IReadOnlyList<DirectoryObjectRef>> Both(Func<IDirectoryLookup, Task<IReadOnlyList<DirectoryObjectRef>>> query)
    {
        var first = await query(primary).ConfigureAwait(false);
        var second = await query(secondary).ConfigureAwait(false);
        return first.Concat(second).DistinctBy(h => h.ObjectGuid).ToList();
    }
}
