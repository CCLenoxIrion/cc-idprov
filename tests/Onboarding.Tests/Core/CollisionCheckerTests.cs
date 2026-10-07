using Onboarding.Core.Domain;
using Onboarding.Core.Naming;
using Onboarding.Tests.TestSupport;

namespace Onboarding.Tests.Core;

public sealed class CollisionCheckerTests
{
    private static readonly Guid RequestId = Guid.NewGuid();
    private readonly FakeDirectory _directory = new();

    private static DerivedIdentity Identity(string? extension = "12") =>
        IdentityDeriver.Derive(TestConfig.Person(extension: extension), null, TestConfig.Global(), TestConfig.Area(), TestConfig.Department())
            .Identity!;

    private Task<IReadOnlyList<Collision>> Check(DerivedIdentity? identity = null) =>
        new CollisionChecker(_directory, _directory).CheckAsync(RequestId, identity ?? Identity());

    [Fact]
    public async Task No_collision()
    {
        _directory.Add(DirectoryObjectClass.User, "Other", sam: "lirion2", mail: "l.irion2@cleancontrolling.de");

        Assert.Empty(await Check());
    }

    [Fact]
    public async Task Sam_collision_is_case_insensitive()
    {
        _directory.Add(DirectoryObjectClass.User, "Lisa Irion", sam: "LIRION");

        var collisions = await Check();

        Assert.Single(collisions);
        Assert.Equal(CollisionField.SamAccountName, collisions[0].Field);
        Assert.Equal(CollisionSource.Directory, collisions[0].Source);
    }

    [Fact]
    public async Task Mail_collision_with_other_users_upn()
    {
        _directory.Add(DirectoryObjectClass.User, "Lisa Irion", sam: "lisairion", upn: "l.irion@cleancontrolling.de");

        var collisions = await Check();

        Assert.Contains(collisions, c => c.Field == CollisionField.Mail);
    }

    [Theory]
    [InlineData(DirectoryObjectClass.Group)]
    [InlineData(DirectoryObjectClass.Contact)]
    [InlineData(DirectoryObjectClass.SharedMailbox)]
    [InlineData(DirectoryObjectClass.User)]
    public async Task Proxy_address_collision_with_any_object_class(DirectoryObjectClass objectClass)
    {
        // Secondary alias .com is used as proxy address by another object.
        _directory.Add(objectClass, "Owner", proxyAddresses: "smtp:l.irion@cleancontrolling.com");

        var collisions = await Check();

        var collision = Assert.Single(collisions);
        Assert.Equal(CollisionField.ProxyAddress, collision.Field);
        Assert.Contains(objectClass.ToString(), collision.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Primary_address_used_as_proxy_of_a_group_collides()
    {
        _directory.Add(DirectoryObjectClass.Group, "GG-Irion", proxyAddresses: "SMTP:L.Irion@cleancontrolling.de");

        var collisions = await Check();

        Assert.Contains(collisions, c => c.Field == CollisionField.Mail);
    }

    [Fact]
    public async Task Phone_number_collision()
    {
        _directory.Add(DirectoryObjectClass.User, "Someone", phone: "+49746592967812");

        var collisions = await Check();

        Assert.Contains(collisions, c => c.Field == CollisionField.PhoneNumber);
    }

    [Fact]
    public async Task Collision_with_open_request()
    {
        _directory.OpenRequests.Add(new OpenRequestIdentity(
            Guid.NewGuid(), "lirion", "lenox.irion@cleancontrolling.de", "lenox.irion@cleancontrolling.de",
            ["SMTP:lenox.irion@cleancontrolling.de", "smtp:l.irion@cleancontrolling.com"], "+49746592967812"));

        var collisions = await Check();

        Assert.All(collisions, c => Assert.Equal(CollisionSource.OpenRequest, c.Source));
        Assert.Contains(collisions, c => c.Field == CollisionField.SamAccountName);
        Assert.Contains(collisions, c => c.Field == CollisionField.ProxyAddress);
        Assert.Contains(collisions, c => c.Field == CollisionField.PhoneNumber);
    }

    [Fact]
    public async Task Own_request_is_excluded()
    {
        var id = Identity();
        _directory.OpenRequests.Add(new OpenRequestIdentity(
            RequestId, id.SamAccountName, id.Mail, id.UserPrincipalName, id.ProxyAddresses, id.PhoneE164));

        Assert.Empty(await Check(id));
    }

    [Fact]
    public async Task Own_account_by_stored_object_guid_is_not_a_collision()
    {
        var own = _directory.Add(DirectoryObjectClass.User, "Lenox Irion", sam: "lirion", mail: "l.irion@cleancontrolling.de",
            upn: "l.irion@cleancontrolling.de", phone: "+49746592967812",
            "SMTP:l.irion@cleancontrolling.de", "smtp:l.irion@cleancontrolling.com");

        var collisions = await new CollisionChecker(_directory, _directory).CheckAsync(RequestId, Identity(), own.Id);

        Assert.Empty(collisions);
    }

    [Fact]
    public async Task Own_account_by_request_id_attribute_is_not_a_collision()
    {
        // Account created but objectGUID not stored (crash in between): matched via request-id attribute.
        _directory.Add(DirectoryObjectClass.User, "Lenox Irion", sam: "lirion", requestId: RequestId);
        _directory.Add(DirectoryObjectClass.User, "Fremd", sam: "lirion", requestId: Guid.NewGuid());

        var collisions = await Check();

        var collision = Assert.Single(collisions);
        Assert.Contains("Fremd", collision.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collision_leads_to_needs_input()
    {
        _directory.Add(DirectoryObjectClass.User, "Lisa Irion", sam: "lirion");
        var derivation = IdentityDeriver.Derive(TestConfig.Person(), null, TestConfig.Global(), TestConfig.Area(), TestConfig.Department());

        var check = IdentityCheck.From(derivation, await Check(derivation.Identity));

        Assert.True(check.NeedsInput);
        Assert.Contains(check.Issues, i => i.Code == IdentityIssueCode.Collision);
    }

    [Theory]
    [InlineData("SMTP:a@b.de", "a@b.de")]
    [InlineData("smtp:a@b.de", "a@b.de")]
    [InlineData("X500:/o=foo", null)]
    [InlineData("sip:a@b.de", null)]
    public void Smtp_address(string proxy, string? expected) => Assert.Equal(expected, CollisionChecker.SmtpAddress(proxy));
}
