using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Identity.Client;
using Onboarding.Steps.Ldap;

namespace Onboarding.Steps.Graph;

/// <summary>
/// Read-only app registration of the web (<c>Integrations:Read:GraphAuth</c>, DECISIONS X6/X16).
/// Identifiers only; the private key stays in <c>LocalMachine\My</c>.
/// </summary>
public sealed partial class GraphReadOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string CertificateThumbprint { get; set; } = "";
    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0/";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    public void Validate()
    {
        if (!Guid.TryParse(TenantId, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException("Integrations:Read:GraphAuth:TenantId must be a GUID.");
        }

        if (!Guid.TryParse(ClientId, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException("Integrations:Read:GraphAuth:ClientId must be a GUID.");
        }

        if (!Thumbprint().IsMatch(CertificateThumbprint))
        {
            throw new InvalidOperationException("Integrations:Read:GraphAuth:CertificateThumbprint must be 40 hex characters.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !BaseUrl.EndsWith('/'))
        {
            throw new InvalidOperationException("Integrations:Read:GraphAuth:BaseUrl must be an https URL ending with '/'.");
        }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{40}$")]
    private static partial Regex Thumbprint();
}

/// <summary>Access token for Graph (app-only). Never logged.</summary>
public interface IGraphTokenSource
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>MSAL client-credentials flow with the certificate from the machine store.</summary>
public sealed class MsalGraphTokenSource(GraphReadOptions options) : IGraphTokenSource, IDisposable
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];
    private readonly Lazy<(IConfidentialClientApplication App, X509Certificate2 Certificate)> _client = new(() => Create(options));

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.Value.App.AcquireTokenForClient(Scopes).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            // MSAL messages can contain tenant/app ids; only the error code leaves.
            throw new DirectoryQueryException($"Graph-Anmeldung fehlgeschlagen ({ex.ErrorCode}).", ex);
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Certificate.Dispose();
        }
    }

    private static (IConfidentialClientApplication, X509Certificate2) Create(GraphReadOptions options)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certificate = store.Certificates.Find(X509FindType.FindByThumbprint, options.CertificateThumbprint, validOnly: false)
                              .OfType<X509Certificate2>().FirstOrDefault()
                          ?? throw new DirectoryQueryException("Zertifikat der Graph-Lese-App nicht gefunden (Thumbprint prüfen).");
        var app = ConfidentialClientApplicationBuilder.Create(options.ClientId)
            .WithTenantId(options.TenantId)
            .WithCertificate(certificate)
            .Build();
        return (app, certificate);
    }
}

/// <summary>
/// Minimal read-only Graph client (GET only). 404 → null; other errors → DirectoryQueryException
/// with the status code only (no URL, no values).
/// </summary>
public sealed class GraphReadClient(HttpClient http, IGraphTokenSource tokens, GraphReadOptions options)
{
    private const int MaxPages = 20;

    /// <summary>
    /// GET with paging (<c>@odata.nextLink</c>), returns all <c>value</c> items. With
    /// <paramref name="advancedQuery"/> the header <c>ConsistencyLevel: eventual</c> is sent
    /// (required together with <c>$count=true</c> for lambda filters on proxyAddresses).
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetListAsync(string relativeUrl, bool advancedQuery, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var url = new Uri(new Uri(options.BaseUrl), relativeUrl);
        for (var page = 0; page < MaxPages && url is not null; page++)
        {
            using var document = await SendAsync(url, advancedQuery, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                break;
            }

            if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                items.AddRange(value.EnumerateArray().Select(e => e.Clone()));
            }

            url = document.RootElement.TryGetProperty("@odata.nextLink", out var next) && next.GetString() is { } link
                ? new Uri(link)
                : null;
        }

        return items;
    }

    private async Task<JsonDocument?> SendAsync(Uri url, bool advancedQuery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false));
        if (advancedQuery)
        {
            request.Headers.Add("ConsistencyLevel", "eventual");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DirectoryQueryException("Graph nicht erreichbar.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DirectoryQueryException("Graph-Abfrage: Zeitüberschreitung.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DirectoryQueryException($"Graph-Abfrage fehlgeschlagen (HTTP {(int)response.StatusCode}).");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
