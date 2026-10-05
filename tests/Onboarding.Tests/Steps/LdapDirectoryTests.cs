using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Onboarding.Core.Configuration;
using Onboarding.Core.Directory;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;
using Onboarding.Steps;
using Onboarding.Steps.Execution;
using Onboarding.Steps.Ldap;

namespace Onboarding.Tests.Steps;

public sealed class LdapFilterTests
{
    [Theory]
    [InlineData("lirion", "lirion")]
    [InlineData("*", @"\2a")]
    [InlineData("a*)(objectClass=*", @"a\2a\29\28objectClass=\2a")]
    [InlineData(@"back\slash", @"back\5cslash")]
    [InlineData("nul\0", @"nul\00")]
    [InlineData("Müller", "Müller")]
    [InlineData("", "")]
    public void Escape_follows_rfc4515(string value, string expected) =>
        Assert.Equal(expected, LdapFilter.Escape(value));

    [Fact]
    public void Guid_is_escaped_in_ad_byte_order()
    {
        var guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        Assert.Equal(@"\67\45\23\01\ab\89\ef\cd\01\23\45\67\89\ab\cd\ef", LdapFilter.EscapeGuid(guid));
    }

    [Theory]
    [InlineData("example.test", "DC=example,DC=test")]
    [InlineData("corp.example.test.", "DC=corp,DC=example,DC=test")]
    public void Domain_to_base_dn(string domain, string expected) =>
        Assert.Equal(expected, LdapFilter.DomainToBaseDn(domain));

    [Theory]
    [InlineData("exa)mple.test")]
    [InlineData("example..test")]
    public void Invalid_domain_is_a_configuration_error(string domain) =>
        Assert.Throws<InvalidOperationException>(() => LdapFilter.DomainToBaseDn(domain));

    [Theory]
    [InlineData("extensionAttribute15", true)]
    [InlineData("msDS-cloudExtensionAttribute1", true)]
    [InlineData("mail)(objectClass=*", false)]
    [InlineData("1abc", false)]
    public void Attribute_names_from_configuration_are_validated(string name, bool valid)
    {
        if (valid)
        {
            Assert.Equal(name, LdapFilter.AttributeName(name));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => LdapFilter.AttributeName(name));
        }
    }
}

public sealed class PhoneNumberMatcherTests
{
    private const string E164 = "+4974659296780";

    [Theory]
    [InlineData("+49 7465 929678-0")]
    [InlineData("+49 (7465) 929678 0")]
    [InlineData("074659296780")]
    [InlineData("+49 (0) 7465 929678-0")]
    [InlineData("0049 7465 929678 0")]
    [InlineData("+4974659296780")]
    public void Matches_display_formats(string stored) =>
        Assert.True(PhoneNumberMatcher.Matches(stored, E164));

    [Theory]
    [InlineData("+49 7465 929678-10")]
    [InlineData("+49 7465 929678-20")]
    [InlineData("0746592967810")]
    [InlineData("+49 7465 123456-0")]
    [InlineData("+43 7465 929678-0")]
    [InlineData("929678-0")]
    [InlineData("")]
    [InlineData(null)]
    public void Different_numbers_do_not_match(string? stored) =>
        Assert.False(PhoneNumberMatcher.Matches(stored, E164));
}

public sealed class LdapDirectoryTests
{
    private const string BaseDn = "DC=example,DC=test";
    private static readonly Guid RequestId = Guid.Parse("6f1c2e3d-4b5a-4c6d-8e9f-0a1b2c3d4e5f");

    private sealed class FakeSearcher(Func<LdapSearchRequest, IEnumerable<LdapEntry>> respond) : ILdapSearcher
    {
        public List<(string Server, LdapSearchRequest Request)> Requests { get; } = [];

        public Task<IReadOnlyList<LdapEntry>> SearchAsync(string server, LdapSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add((server, request));
            return Task.FromResult<IReadOnlyList<LdapEntry>>(respond(request).ToList());
        }
    }

    private static LdapDirectory Directory(FakeSearcher searcher, string requestIdAttribute = "extensionAttribute15") =>
        new(searcher, _ => Task.FromResult(new LdapDirectorySettings("dc01.example.test", BaseDn, requestIdAttribute)));

    private static LdapEntry Entry(string name, string[] objectClass, Dictionary<string, string[]>? extra = null, Guid? guid = null)
    {
        var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectClass"] = objectClass,
            ["displayName"] = [name],
        };
        foreach (var (key, value) in extra ?? [])
        {
            values[key] = value;
        }

        return LdapEntry.FromStrings($"CN={name},{BaseDn}", values, guid ?? Guid.NewGuid());
    }

    private static readonly string[] UserClass = ["top", "person", "organizationalPerson", "user"];

    [Fact]
    public async Task Sam_search_escapes_the_value_and_reads_request_id()
    {
        var guid = Guid.NewGuid();
        var searcher = new FakeSearcher(_ => [Entry("Lenox Irion", UserClass, new() { ["extensionAttribute15"] = [RequestId.ToString()] }, guid)]);

        var hits = await Directory(searcher).FindBySamAccountNameAsync("li*)(x", default);

        var (server, request) = Assert.Single(searcher.Requests);
        Assert.Equal("dc01.example.test", server);
        Assert.Equal(@"(sAMAccountName=li\2a\29\28x)", request.Filter);
        Assert.Equal(BaseDn, request.BaseDn);
        Assert.Equal(LdapScope.Subtree, request.Scope);
        Assert.Contains("extensionAttribute15", request.Attributes);
        var hit = Assert.Single(hits);
        Assert.Equal(new DirectoryObjectRef(guid, DirectoryObjectClass.User, "Lenox Irion", RequestId), hit);
    }

    [Fact]
    public async Task Mail_and_proxy_filters()
    {
        var searcher = new FakeSearcher(_ => []);
        var directory = Directory(searcher);

        await directory.FindByMailOrUpnAsync("l.irion@example.test", default);
        await directory.FindProxyAddressOwnersAsync("l.irion@example.test", default);

        Assert.Equal("(|(mail=l.irion@example.test)(userPrincipalName=l.irion@example.test))", searcher.Requests[0].Request.Filter);
        Assert.Equal("(proxyAddresses=smtp:l.irion@example.test)", searcher.Requests[1].Request.Filter);
    }

    [Theory]
    [InlineData(new[] { "top", "group" }, null, DirectoryObjectClass.Group)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "contact" }, null, DirectoryObjectClass.Contact)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "user" }, "4", DirectoryObjectClass.SharedMailbox)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "user" }, "34359738368", DirectoryObjectClass.SharedMailbox)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "user" }, "1", DirectoryObjectClass.User)]
    [InlineData(new[] { "top", "person", "organizationalPerson", "user", "computer" }, null, DirectoryObjectClass.Other)]
    public async Task Object_classes_are_mapped(string[] objectClass, string? recipientType, DirectoryObjectClass expected)
    {
        var extra = recipientType is null ? null : new Dictionary<string, string[]> { ["msExchRecipientTypeDetails"] = [recipientType] };
        var searcher = new FakeSearcher(_ => [Entry("X", objectClass, extra)]);

        var hit = Assert.Single(await Directory(searcher).FindProxyAddressOwnersAsync("x@example.test", default));

        Assert.Equal(expected, hit.ObjectClass);
        Assert.Null(hit.ProvisionedForRequestId);
    }

    [Fact]
    public async Task Phone_search_uses_the_extension_and_compares_digits()
    {
        var formats = new[] { "+49 7465 929678-0", "+49 (7465) 929678 0", "074659296780" };
        var searcher = new FakeSearcher(_ =>
            formats.Select(f => Entry(f, UserClass, new() { ["telephoneNumber"] = [f] }))
                .Append(Entry("Andere Durchwahl", UserClass, new() { ["telephoneNumber"] = ["+49 7465 929678-10"] }))
                .Append(Entry("Andere Nummer", UserClass, new() { ["telephoneNumber"] = ["+49 7465 123456-0"] })));

        var hits = await Directory(searcher).FindByPhoneNumberAsync("+4974659296780", "0", default);

        Assert.Equal("(telephoneNumber=*0*)", Assert.Single(searcher.Requests).Request.Filter);
        Assert.Contains("telephoneNumber", searcher.Requests[0].Request.Attributes);
        Assert.Equal(formats, hits.Select(h => h.DisplayName));
    }

    [Fact]
    public async Task Phone_search_without_stored_extension_uses_the_last_digits()
    {
        var searcher = new FakeSearcher(_ => []);
        await Directory(searcher).FindByPhoneNumberAsync("+4974659296780", null, default);
        Assert.Equal("(telephoneNumber=*6780*)", Assert.Single(searcher.Requests).Request.Filter);
    }

    [Fact]
    public async Task Manager_search_escapes_query_limits_and_skips_disabled_accounts()
    {
        var guid = Guid.NewGuid();
        var searcher = new FakeSearcher(_ =>
        [
            Entry("Zora Zett", UserClass, new() { ["mail"] = ["z.zett@example.test"] }),
            Entry("Anna Alt", UserClass, guid: guid),
        ]);

        var users = await Directory(searcher).SearchUsersAsync("a*", 25, default);

        var request = Assert.Single(searcher.Requests).Request;
        Assert.Contains(@"(displayName=*a\2a*)", request.Filter, StringComparison.Ordinal);
        Assert.Contains("(!(userAccountControl:1.2.840.113556.1.4.803:=2))", request.Filter, StringComparison.Ordinal);
        Assert.Equal(25, request.SizeLimit);
        Assert.Equal(["Anna Alt", "Zora Zett"], users.Select(u => u.DisplayName));
        Assert.Equal(guid, users[0].ObjectGuid);
        Assert.Equal("z.zett@example.test", users[1].Mail);
    }

    [Fact]
    public async Task Get_user_by_guid_uses_binary_filter()
    {
        var guid = Guid.NewGuid();
        var searcher = new FakeSearcher(_ => [Entry("Anna Alt", UserClass, guid: guid)]);

        var user = await Directory(searcher).GetUserAsync(guid, default);

        Assert.Contains($"(objectGUID={LdapFilter.EscapeGuid(guid)})", Assert.Single(searcher.Requests).Request.Filter, StringComparison.Ordinal);
        Assert.Equal(guid, user!.ObjectGuid);
    }

    [Fact]
    public async Task Organizational_units_and_existence()
    {
        var searcher = new FakeSearcher(request => request.Scope == LdapScope.Base && request.BaseDn.StartsWith("OU=Fehlt", StringComparison.Ordinal)
            ? []
            : [LdapEntry.FromStrings("OU=Users,OU=Technical," + BaseDn, new Dictionary<string, string[]> { ["canonicalName"] = ["example.test/Technical/Users"] })]);
        var directory = Directory(searcher);

        var ous = await directory.GetOrganizationalUnitsAsync(default);
        Assert.Equal("example.test/Technical/Users", Assert.Single(ous).CanonicalName);
        Assert.True(await directory.OrganizationalUnitExistsAsync("OU=Users,OU=Technical," + BaseDn, default));
        Assert.False(await directory.OrganizationalUnitExistsAsync("OU=Fehlt," + BaseDn, default));
        Assert.False(await directory.OrganizationalUnitExistsAsync("  ", default));
        Assert.Equal(LdapScope.Base, searcher.Requests[^1].Request.Scope);
    }

    [Theory]
    [InlineData("12", "1", 12, true)]
    [InlineData("8", "0", 8, false)]
    [InlineData("10", "17", 10, true)]
    public async Task Password_policy_is_read_from_the_domain_root(string minLength, string properties, int expectedLength, bool complexity)
    {
        var searcher = new FakeSearcher(_ => [LdapEntry.FromStrings(BaseDn, new Dictionary<string, string[]>
        {
            ["minPwdLength"] = [minLength],
            ["pwdProperties"] = [properties],
        })]);

        var policy = await ((IPasswordPolicyProvider)Directory(searcher)).GetAsync(default);

        Assert.Equal(new PasswordPolicy(expectedLength, complexity), policy);
        var request = Assert.Single(searcher.Requests).Request;
        Assert.Equal(LdapScope.Base, request.Scope);
        Assert.Equal(BaseDn, request.BaseDn);
    }

    [Fact]
    public async Task Invalid_request_id_attribute_is_a_configuration_error()
    {
        var searcher = new FakeSearcher(_ => []);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Directory(searcher, "mail)(x").FindBySamAccountNameAsync("lirion", default));
    }
}

public sealed class ReadIntegrationRegistrationTests
{
    private static ServiceCollection Register(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))
                .Append(KeyValuePair.Create("Integrations:FakeDataFile", (string?)"fake.json")))
            .Build();
        var services = new ServiceCollection();
        services.AddOnboardingIntegrations(configuration, (_, _) => Task.FromResult(new GlobalConfig
        {
            DomainFqdn = "example.test",
            RequestIdAttribute = "extensionAttribute15",
        }));
        return services;
    }

    [Fact]
    public void Real_directory_registers_the_ldap_adapter()
    {
        using var provider = Register(("Integrations:Read:Directory", "Real")).BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<LdapDirectory>(scope.ServiceProvider.GetRequiredService<IDirectoryLookup>());
        Assert.IsType<LdapDirectory>(scope.ServiceProvider.GetRequiredService<IDirectoryBrowser>());
        Assert.IsType<LdapDirectory>(scope.ServiceProvider.GetRequiredService<IPasswordPolicyProvider>());
        Assert.IsType<LdapConnectionSearcher>(scope.ServiceProvider.GetRequiredService<ILdapSearcher>());
    }

    [Theory]
    [InlineData("Integrations:Read:Directory", "DryRun")]
    [InlineData("Integrations:Read:Directory", "Ldap")]
    [InlineData("Integrations:Mode", "Fake")]
    public void Invalid_or_legacy_settings_fail_at_startup(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => Register((key, value)));

    [Fact]
    public void Legacy_licenses_key_fails_at_startup() =>
        Assert.Throws<InvalidOperationException>(() => Register(("Integrations:Read:Licenses", "Fake")));

    [Fact]
    public void Fake_is_the_default() =>
        Assert.Equal(IntegrationMode.Fake, IntegrationRegistration.ReadReadMode(new ConfigurationBuilder().Build(), "Directory"));
}
