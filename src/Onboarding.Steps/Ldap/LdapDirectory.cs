using Onboarding.Core.Directory;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;

namespace Onboarding.Steps.Ldap;

/// <summary>Values from the global configuration that the read adapter needs.</summary>
/// <param name="Server">Domain controller or domain name to connect to.</param>
/// <param name="BaseDn">Search base (domain root).</param>
/// <param name="RequestIdAttribute">AD attribute holding the request id (DECISIONS K6); empty = none.</param>
public sealed record LdapDirectorySettings(string Server, string BaseDn, string RequestIdAttribute);

/// <summary>
/// Read-only AD adapter for the web (collision checks, manager search, OU picker, password
/// policy). Runs under the web service account; never writes (DECISIONS X6). All filter values
/// are escaped with <see cref="LdapFilter"/> (X9).
/// </summary>
public sealed class LdapDirectory(ILdapSearcher searcher, Func<CancellationToken, Task<LdapDirectorySettings>> settings)
    : IDirectoryLookup, IDirectoryBrowser, IPasswordPolicyProvider
{
    /// <summary>msExchRecipientTypeDetails of shared mailboxes (on-prem and remote/hybrid).</summary>
    private const long SharedMailbox = 4;

    private const long RemoteSharedMailbox = 34359738368;

    /// <summary>Only enabled accounts (userAccountControl bit ACCOUNTDISABLE not set).</summary>
    private const string EnabledUserFilter = "(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))";

    private static readonly string[] UserAttributes = ["objectGUID", "displayName", "name", "mail"];
    private static readonly string[] OuAttributes = ["canonicalName"];
    private static readonly string[] PolicyAttributes = ["minPwdLength", "pwdProperties"];

    public Task<IReadOnlyList<DirectoryObjectRef>> FindBySamAccountNameAsync(string samAccountName, CancellationToken cancellationToken) =>
        FindObjectsAsync($"(sAMAccountName={LdapFilter.Escape(samAccountName)})", cancellationToken);

    public Task<IReadOnlyList<DirectoryObjectRef>> FindByMailOrUpnAsync(string address, CancellationToken cancellationToken)
    {
        var value = LdapFilter.Escape(address);
        return FindObjectsAsync($"(|(mail={value})(userPrincipalName={value}))", cancellationToken);
    }

    /// <summary>AD matches proxyAddresses case-insensitively, so "smtp:" also finds "SMTP:".</summary>
    public Task<IReadOnlyList<DirectoryObjectRef>> FindProxyAddressOwnersAsync(string address, CancellationToken cancellationToken) =>
        FindObjectsAsync($"(proxyAddresses={LdapFilter.Escape("smtp:" + address)})", cancellationToken);

    /// <summary>
    /// telephoneNumber is free text, so the search uses the extension as substring and the
    /// exact comparison happens on the digits (DECISIONS X9).
    /// </summary>
    public async Task<IReadOnlyList<DirectoryObjectRef>> FindByPhoneNumberAsync(string e164, string? extension, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e164);
        var term = string.IsNullOrWhiteSpace(extension) ? LastDigits(e164) : extension.Trim();
        if (term.Length == 0)
        {
            return [];
        }

        var config = await settings(cancellationToken).ConfigureAwait(false);
        var entries = await searcher.SearchAsync(config.Server, new LdapSearchRequest(
            config.BaseDn,
            $"(telephoneNumber=*{LdapFilter.Escape(term)}*)",
            LdapScope.Subtree,
            ObjectAttributes(config, "telephoneNumber")), cancellationToken).ConfigureAwait(false);
        return entries
            .Where(e => e.GetStrings("telephoneNumber").Any(number => PhoneNumberMatcher.Matches(number, e164)))
            .Select(e => ToRef(e, config))
            .ToList();
    }

    public async Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var filter = string.IsNullOrWhiteSpace(query)
            ? $"(&{EnabledUserFilter})"
            : $"(&{EnabledUserFilter}(|(displayName=*{LdapFilter.Escape(query.Trim())}*)(sAMAccountName=*{LdapFilter.Escape(query.Trim())}*)(mail=*{LdapFilter.Escape(query.Trim())}*)))";
        var config = await settings(cancellationToken).ConfigureAwait(false);
        var entries = await searcher.SearchAsync(config.Server,
            new LdapSearchRequest(config.BaseDn, filter, LdapScope.Subtree, UserAttributes, maxResults), cancellationToken).ConfigureAwait(false);
        return entries
            .Select(ToUser)
            .OfType<DirectoryUser>()
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<DirectoryUser?> GetUserAsync(Guid objectGuid, CancellationToken cancellationToken)
    {
        var config = await settings(cancellationToken).ConfigureAwait(false);
        var entries = await searcher.SearchAsync(config.Server, new LdapSearchRequest(
            config.BaseDn,
            $"(&(objectCategory=person)(objectClass=user)(objectGUID={LdapFilter.EscapeGuid(objectGuid)}))",
            LdapScope.Subtree,
            UserAttributes,
            SizeLimit: 1), cancellationToken).ConfigureAwait(false);
        return entries.Select(ToUser).OfType<DirectoryUser>().FirstOrDefault();
    }

    public async Task<IReadOnlyList<OrganizationalUnit>> GetOrganizationalUnitsAsync(CancellationToken cancellationToken)
    {
        var config = await settings(cancellationToken).ConfigureAwait(false);
        var entries = await searcher.SearchAsync(config.Server, new LdapSearchRequest(
            config.BaseDn, "(objectClass=organizationalUnit)", LdapScope.Subtree, OuAttributes), cancellationToken).ConfigureAwait(false);
        return entries
            .Select(e => new OrganizationalUnit(e.DistinguishedName, e.GetString("canonicalName") ?? e.DistinguishedName))
            .OrderBy(ou => ou.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Base search on the DN itself; a missing or malformed DN is "does not exist".</summary>
    public async Task<bool> OrganizationalUnitExistsAsync(string distinguishedName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return false;
        }

        var config = await settings(cancellationToken).ConfigureAwait(false);
        var entries = await searcher.SearchAsync(config.Server, new LdapSearchRequest(
            distinguishedName.Trim(), "(objectClass=organizationalUnit)", LdapScope.Base, OuAttributes, SizeLimit: 1), cancellationToken).ConfigureAwait(false);
        return entries.Count > 0;
    }

    /// <summary>
    /// Default domain policy from the domain root, like <c>Get-ADDefaultDomainPasswordPolicy</c>
    /// (fine-grained policies are not considered).
    /// </summary>
    public async Task<PasswordPolicy> GetAsync(CancellationToken cancellationToken)
    {
        var config = await settings(cancellationToken).ConfigureAwait(false);
        var entries = await searcher.SearchAsync(config.Server, new LdapSearchRequest(
            config.BaseDn, "(objectClass=domainDNS)", LdapScope.Base, PolicyAttributes, SizeLimit: 1), cancellationToken).ConfigureAwait(false);
        var domain = (entries.Count > 0 ? entries[0] : null)
                     ?? throw new DirectoryQueryException("Domänen-Kennwortrichtlinie nicht lesbar.");
        var minLength = domain.GetInt64("minPwdLength") ?? throw new DirectoryQueryException("Domänen-Kennwortrichtlinie nicht lesbar.");
        const long complexityFlag = 1; // DOMAIN_PASSWORD_COMPLEX
        return new PasswordPolicy((int)minLength, ((domain.GetInt64("pwdProperties") ?? 0) & complexityFlag) != 0);
    }

    private async Task<IReadOnlyList<DirectoryObjectRef>> FindObjectsAsync(string filter, CancellationToken cancellationToken)
    {
        var config = await settings(cancellationToken).ConfigureAwait(false);
        var entries = await searcher.SearchAsync(config.Server,
            new LdapSearchRequest(config.BaseDn, filter, LdapScope.Subtree, ObjectAttributes(config)), cancellationToken).ConfigureAwait(false);
        return entries.Select(e => ToRef(e, config)).ToList();
    }

    private static string[] ObjectAttributes(LdapDirectorySettings config, params string[] extra)
    {
        var attributes = new List<string> { "objectGUID", "objectClass", "displayName", "name", "msExchRecipientTypeDetails" };
        if (!string.IsNullOrWhiteSpace(config.RequestIdAttribute))
        {
            attributes.Add(LdapFilter.AttributeName(config.RequestIdAttribute));
        }

        attributes.AddRange(extra);
        return [.. attributes];
    }

    private static DirectoryObjectRef ToRef(LdapEntry entry, LdapDirectorySettings config)
    {
        Guid? requestId = null;
        if (!string.IsNullOrWhiteSpace(config.RequestIdAttribute) &&
            Guid.TryParse(entry.GetString(config.RequestIdAttribute), out var parsed))
        {
            requestId = parsed;
        }

        return new DirectoryObjectRef(
            entry.GetGuid("objectGUID") ?? Guid.Empty,
            ClassOf(entry),
            entry.GetString("displayName") ?? entry.GetString("name") ?? entry.DistinguishedName,
            requestId);
    }

    private static DirectoryObjectClass ClassOf(LdapEntry entry)
    {
        var classes = entry.GetStrings("objectClass");
        bool Has(string name) => classes.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (Has("computer"))
        {
            return DirectoryObjectClass.Other;
        }

        if (Has("user"))
        {
            return entry.GetInt64("msExchRecipientTypeDetails") is SharedMailbox or RemoteSharedMailbox
                ? DirectoryObjectClass.SharedMailbox
                : DirectoryObjectClass.User;
        }

        return Has("group") ? DirectoryObjectClass.Group
            : Has("contact") ? DirectoryObjectClass.Contact
            : DirectoryObjectClass.Other;
    }

    private static DirectoryUser? ToUser(LdapEntry entry) =>
        entry.GetGuid("objectGUID") is { } guid
            ? new DirectoryUser(guid, entry.GetString("displayName") ?? entry.GetString("name") ?? entry.DistinguishedName, entry.GetString("mail"), entry.DistinguishedName)
            : null;

    /// <summary>Fallback search term without stored extension: the last four digits.</summary>
    private static string LastDigits(string e164)
    {
        var digits = PhoneNumberMatcher.Digits(e164);
        return digits.Length <= 4 ? digits : digits[^4..];
    }
}
