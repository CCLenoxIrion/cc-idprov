using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Onboarding.Core.Configuration;
using Onboarding.Core.Directory;
using Onboarding.Core.Naming;
using Onboarding.Steps;
using Onboarding.Steps.Graph;
using Onboarding.Steps.Ldap;

namespace Onboarding.Tests.Steps;

public sealed class GraphReadTests
{
    private const string Token = "eyJ0eXAi.GEHEIMER-TOKEN";

    private sealed class StaticToken : IGraphTokenSource
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(Token);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GraphReadClient Client, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var options = new GraphReadOptions();
        return (new GraphReadClient(new HttpClient(handler), new StaticToken(), options), handler);
    }

    private static string Decoded(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);

    [Fact]
    public async Task Licenses_are_read_from_subscribed_skus()
    {
        var (client, handler) = Create(_ => Json("""
            {"value":[{"skuPartNumber":"SPB","prepaidUnits":{"enabled":25},"consumedUnits":24},
                      {"skuPartNumber":"MCOEV","prepaidUnits":{"enabled":5},"consumedUnits":5}]}
            """));

        var licenses = await new GraphLicenseOverview(client).GetAsync(default);

        Assert.Equal([new LicenseAvailability("MCOEV", 5, 5), new LicenseAvailability("SPB", 25, 24)], licenses);
        Assert.Equal(0, licenses[0].Free);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.StartsWith("/v1.0/subscribedSkus", request.RequestUri!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_lookup_uses_advanced_query_with_both_prefixes_and_contacts_by_mail()
    {
        var (client, handler) = Create(_ => Json("""{"value":[]}"""));

        await new EntraDirectoryLookup(client).FindProxyAddressOwnersAsync("o'brien@example.test", default);

        Assert.Equal(3, handler.Requests.Count);
        foreach (var request in handler.Requests.Take(2))
        {
            Assert.Equal("eventual", Assert.Single(request.Headers.GetValues("ConsistencyLevel")));
            var url = Decoded(request);
            Assert.Contains("$count=true", url, StringComparison.Ordinal);
            Assert.Contains("proxyAddresses/any(p:p eq 'SMTP:o''brien@example.test') or proxyAddresses/any(p:p eq 'smtp:o''brien@example.test')", url, StringComparison.Ordinal);
        }

        Assert.StartsWith("/v1.0/users?", handler.Requests[0].RequestUri!.PathAndQuery, StringComparison.Ordinal);
        Assert.StartsWith("/v1.0/groups?", handler.Requests[1].RequestUri!.PathAndQuery, StringComparison.Ordinal);
        var contacts = handler.Requests[2];
        Assert.StartsWith("/v1.0/contacts?", contacts.RequestUri!.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("mail eq 'o''brien@example.test'", Decoded(contacts), StringComparison.Ordinal);
        Assert.False(contacts.Headers.Contains("ConsistencyLevel"));
    }

    [Fact]
    public async Task Filter_values_are_url_encoded()
    {
        var (client, handler) = Create(_ => Json("""{"value":[]}"""));

        await new EntraDirectoryLookup(client).FindByMailOrUpnAsync("a+b&c#d@example.test", default);

        var raw = handler.Requests[0].RequestUri!.OriginalString;
        Assert.DoesNotContain("a+b&c#d", raw, StringComparison.Ordinal);
        Assert.Contains("userPrincipalName eq 'a+b&c#d@example.test' or mail eq 'a+b&c#d@example.test'", Decoded(handler.Requests[0]), StringComparison.Ordinal);
        Assert.Equal("eventual", Assert.Single(handler.Requests[0].Headers.GetValues("ConsistencyLevel")));
    }

    [Fact]
    public async Task Only_cloud_only_objects_are_reported_with_their_class()
    {
        var cloudUser = Guid.NewGuid();
        var cloudGroup = Guid.NewGuid();
        var (client, _) = Create(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/users" => Json("{\"value\":[{\"id\":\"" + cloudUser + "\",\"displayName\":\"Cloud User\",\"onPremisesSyncEnabled\":null}," +
                                  "{\"id\":\"" + Guid.NewGuid() + "\",\"displayName\":\"Synced\",\"onPremisesSyncEnabled\":true}]}"),
            "/v1.0/groups" => Json("{\"value\":[{\"id\":\"" + cloudGroup + "\",\"displayName\":\"M365-Gruppe\"}]}"),
            _ => Json("""{"value":[]}"""),
        });

        var hits = await new EntraDirectoryLookup(client).FindByMailOrUpnAsync("x@example.test", default);

        Assert.Equal(
            [new DirectoryObjectRef(cloudUser, DirectoryObjectClass.User, "Cloud User"), new DirectoryObjectRef(cloudGroup, DirectoryObjectClass.Group, "M365-Gruppe")],
            hits);
    }

    [Fact]
    public async Task Paging_follows_next_link()
    {
        var (client, handler) = Create(request => request.RequestUri!.Query.Contains("skiptoken", StringComparison.Ordinal)
            ? Json("""{"value":[{"skuPartNumber":"B","prepaidUnits":{"enabled":1},"consumedUnits":0}]}""")
            : Json("""{"value":[{"skuPartNumber":"A","prepaidUnits":{"enabled":1},"consumedUnits":0}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/subscribedSkus?$skiptoken=x"}"""));

        var licenses = await new GraphLicenseOverview(client).GetAsync(default);

        Assert.Equal(["A", "B"], licenses.Select(l => l.SkuPartNumber));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
    public async Task Errors_carry_only_the_status_code(HttpStatusCode status, string expected)
    {
        var (client, _) = Create(_ => Json("""{"error":{"message":"Tenant 1111 token GEHEIM"}}""", status));

        var ex = await Assert.ThrowsAsync<DirectoryQueryException>(() => new GraphLicenseOverview(client).GetAsync(default));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GEHEIM", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Composite_merges_and_deduplicates()
    {
        var shared = new DirectoryObjectRef(Guid.NewGuid(), DirectoryObjectClass.User, "AD");
        var cloud = new DirectoryObjectRef(Guid.NewGuid(), DirectoryObjectClass.Group, "Cloud");
        var composite = new CompositeDirectoryLookup(new StaticLookup([shared]), new StaticLookup([shared, cloud]));

        Assert.Equal([shared, cloud], await composite.FindProxyAddressOwnersAsync("x@example.test", default));
    }

    private sealed class StaticLookup(IReadOnlyList<DirectoryObjectRef> hits) : IDirectoryLookup
    {
        public Task<IReadOnlyList<DirectoryObjectRef>> FindBySamAccountNameAsync(string samAccountName, CancellationToken cancellationToken) => Task.FromResult(hits);
        public Task<IReadOnlyList<DirectoryObjectRef>> FindByMailOrUpnAsync(string address, CancellationToken cancellationToken) => Task.FromResult(hits);
        public Task<IReadOnlyList<DirectoryObjectRef>> FindProxyAddressOwnersAsync(string address, CancellationToken cancellationToken) => Task.FromResult(hits);
        public Task<IReadOnlyList<DirectoryObjectRef>> FindByPhoneNumberAsync(string e164, string? extension, CancellationToken cancellationToken) => Task.FromResult(hits);
    }

    private static ServiceCollection Register(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))
                .Append(KeyValuePair.Create("Integrations:FakeDataFile", (string?)"fake.json")))
            .Build();
        var services = new ServiceCollection();
        services.AddOnboardingIntegrations(configuration, (_, _) => Task.FromResult(new GlobalConfig { DomainFqdn = "example.test" }));
        return services;
    }

    private static readonly (string, string)[] ValidGraph =
    [
        ("Integrations:Read:Graph", "Real"),
        ("Integrations:Read:GraphAuth:TenantId", "11111111-1111-1111-1111-111111111111"),
        ("Integrations:Read:GraphAuth:ClientId", "22222222-2222-2222-2222-222222222222"),
        ("Integrations:Read:GraphAuth:CertificateThumbprint", "0123456789ABCDEF0123456789ABCDEF01234567"),
    ];

    [Fact]
    public void Real_graph_registers_license_overview_and_composite_lookup()
    {
        using var provider = Register([.. ValidGraph, ("Integrations:Read:Directory", "Real")]).BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<GraphLicenseOverview>(scope.ServiceProvider.GetRequiredService<ILicenseOverview>());
        Assert.IsType<CompositeDirectoryLookup>(scope.ServiceProvider.GetRequiredService<IDirectoryLookup>());
        Assert.IsType<LdapDirectory>(scope.ServiceProvider.GetRequiredService<IDirectoryBrowser>());
    }

    [Theory]
    [InlineData("Integrations:Read:GraphAuth:TenantId", "x")]
    [InlineData("Integrations:Read:GraphAuth:ClientId", "")]
    [InlineData("Integrations:Read:GraphAuth:CertificateThumbprint", "ABC")]
    [InlineData("Integrations:Read:GraphAuth:BaseUrl", "http://graph.example/")]
    [InlineData("Integrations:Read:Graph", "DryRun")]
    public void Invalid_graph_configuration_fails_at_startup(string key, string value)
    {
        var settings = ValidGraph.ToDictionary(s => s.Item1, s => s.Item2);
        settings[key] = value;
        Assert.Throws<InvalidOperationException>(() => Register([.. settings.Select(s => (s.Key, s.Value))]));
    }
}
