using System.DirectoryServices.Protocols;

namespace Onboarding.Steps.Ldap;

/// <summary>
/// <see cref="ILdapSearcher"/> over System.DirectoryServices.Protocols: Negotiate bind with the
/// identity of the process (web service account, DECISIONS X6), signing and sealing, paged
/// results, no referral chasing. One connection per search keeps it thread-safe.
/// </summary>
public sealed class LdapConnectionSearcher(LdapOptions options) : ILdapSearcher
{
    public Task<IReadOnlyList<LdapEntry>> SearchAsync(string server, LdapSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run<IReadOnlyList<LdapEntry>>(() => Search(server, request, cancellationToken), cancellationToken);
    }

    private List<LdapEntry> Search(string server, LdapSearchRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = new LdapConnection(new LdapDirectoryIdentifier(server, options.Port), credential: null, AuthType.Negotiate);
            connection.Timeout = options.Timeout;
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.Signing = true;
            connection.SessionOptions.Sealing = true;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            connection.Bind();

            var search = new SearchRequest(request.BaseDn, request.Filter, Map(request.Scope), [.. request.Attributes]);
            var page = new PageResultRequestControl(options.PageSize);
            search.Controls.Add(page);
            var results = new List<LdapEntry>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SearchResponse response;
                try
                {
                    response = (SearchResponse)connection.SendRequest(search);
                }
                catch (DirectoryOperationException ex) when (ex.Response?.ResultCode is ResultCode.NoSuchObject or ResultCode.InvalidDNSyntax)
                {
                    return results;
                }

                foreach (SearchResultEntry entry in response.Entries)
                {
                    results.Add(Convert(entry));
                    if (request.SizeLimit > 0 && results.Count >= request.SizeLimit)
                    {
                        return results;
                    }
                }

                var next = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (next is null || next.Cookie.Length == 0)
                {
                    return results;
                }

                page.Cookie = next.Cookie;
            }
        }
        catch (DirectoryOperationException ex)
        {
            throw new DirectoryQueryException($"Verzeichnisabfrage fehlgeschlagen ({ex.Response?.ResultCode}).", ex);
        }
        catch (LdapException ex)
        {
            throw new DirectoryQueryException($"Verzeichnis nicht erreichbar (LDAP-Fehler {ex.ErrorCode}).", ex);
        }
    }

    private static SearchScope Map(LdapScope scope) => scope switch
    {
        LdapScope.Base => SearchScope.Base,
        LdapScope.OneLevel => SearchScope.OneLevel,
        _ => SearchScope.Subtree,
    };

    private static LdapEntry Convert(SearchResultEntry entry)
    {
        var attributes = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in entry.Attributes.AttributeNames)
        {
            attributes[name] = entry.Attributes[name].GetValues(typeof(byte[])).Cast<byte[]>().ToList();
        }

        return new LdapEntry(entry.DistinguishedName, attributes);
    }
}
