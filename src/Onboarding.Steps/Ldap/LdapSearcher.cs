using System.Text;

namespace Onboarding.Steps.Ldap;

public enum LdapScope
{
    Base,
    OneLevel,
    Subtree,
}

/// <summary>One LDAP search. <paramref name="SizeLimit"/> 0 = all results (paged).</summary>
public sealed record LdapSearchRequest(
    string BaseDn,
    string Filter,
    LdapScope Scope,
    IReadOnlyList<string> Attributes,
    int SizeLimit = 0);

/// <summary>A search result entry. Attribute names are case-insensitive; values are raw bytes.</summary>
public sealed class LdapEntry(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<byte[]>> attributes)
{
    public string DistinguishedName { get; } = distinguishedName;

    public IReadOnlyList<byte[]> GetValues(string attribute) =>
        attributes.TryGetValue(attribute, out var values) ? values : [];

    public IReadOnlyList<string> GetStrings(string attribute) =>
        GetValues(attribute).Select(v => Encoding.UTF8.GetString(v)).ToList();

    public string? GetString(string attribute) => GetValues(attribute) is [var first, ..] ? Encoding.UTF8.GetString(first) : null;

    public long? GetInt64(string attribute) =>
        long.TryParse(GetString(attribute), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public Guid? GetGuid(string attribute) =>
        GetValues(attribute) is [{ Length: 16 } bytes, ..] ? new Guid(bytes) : null;

    /// <summary>Builds an entry from string values (tests, fakes).</summary>
    public static LdapEntry FromStrings(string distinguishedName, IReadOnlyDictionary<string, string[]> values, Guid? objectGuid = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var attributes = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, strings) in values)
        {
            attributes[name] = strings.Select(Encoding.UTF8.GetBytes).ToList();
        }

        if (objectGuid is { } guid)
        {
            attributes["objectGUID"] = [guid.ToByteArray()];
        }

        return new LdapEntry(distinguishedName, attributes);
    }
}

/// <summary>
/// Executes read-only LDAP searches. A missing or malformed base DN yields an empty result
/// (not an exception), so "does this OU exist" is a plain search.
/// </summary>
public interface ILdapSearcher
{
    Task<IReadOnlyList<LdapEntry>> SearchAsync(string server, LdapSearchRequest request, CancellationToken cancellationToken);
}

/// <summary>Connection settings (<c>Integrations:Read:Ldap</c>).</summary>
public sealed class LdapOptions
{
    /// <summary>Domain controller or domain name; empty = <c>GlobalConfig.DomainFqdn</c>.</summary>
    public string Server { get; set; } = "";

    public int Port { get; set; } = 389;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    public int PageSize { get; set; } = 500;
}

/// <summary>Raised when the directory cannot be queried. The message contains no query values.</summary>
public sealed class DirectoryQueryException : Exception
{
    public DirectoryQueryException()
    {
    }

    public DirectoryQueryException(string message)
        : base(message)
    {
    }

    public DirectoryQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
