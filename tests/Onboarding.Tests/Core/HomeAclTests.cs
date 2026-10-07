using Onboarding.Core.Configuration;
using Onboarding.Tests.TestSupport;

namespace Onboarding.Tests.Core;

public sealed class HomeAclTests
{
    private static HomeAce Ace(string principal, HomeRight right) => new() { Principal = principal, Right = right };

    [Fact]
    public void Department_entry_wins_for_the_same_principal_and_adds_new_ones()
    {
        var global = TestConfig.Global();
        global.Home.UserRight = HomeRight.FullControl;
        global.Home.AdditionalAces = [Ace("SYSTEM", HomeRight.FullControl), Ace(@"CC\CC-Management", HomeRight.Modify)];
        var department = TestConfig.Department();
        department.HomeAdditionalAces = [Ace(@"cc\cc-management ", HomeRight.FullControl), Ace(@"CC\CC-Management-Lead", HomeRight.FullControl)];

        var acl = HomeAcl.Resolve(global, department);

        Assert.Equal(HomeRight.FullControl, acl.UserRight);
        Assert.Equal(
            [("SYSTEM", HomeRight.FullControl), (@"CC\CC-Management", HomeRight.FullControl), (@"CC\CC-Management-Lead", HomeRight.FullControl)],
            acl.AdditionalAces.Select(a => (a.Principal, a.Right)));
    }

    [Fact]
    public void Without_department_entries_the_global_list_is_used()
    {
        var global = TestConfig.Global();
        global.Home.AdditionalAces = [Ace("SYSTEM", HomeRight.FullControl)];

        var acl = HomeAcl.Resolve(global, TestConfig.Department());

        Assert.Equal(HomeRight.Modify, acl.UserRight);
        Assert.Equal("SYSTEM", Assert.Single(acl.AdditionalAces).Principal);
    }

    [Theory]
    [InlineData("", "Principal fehlt")]
    [InlineData("CC\\X=FullControl", "ungültig")]
    [InlineData("CC\\X\nY", "ungültig")]
    public void Invalid_principals_are_reported(string principal, string expected) =>
        Assert.Contains(HomeAcl.Validate([Ace(principal, HomeRight.Modify)], "Test"), e => e.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void Duplicate_principals_are_reported_in_global_and_department_config()
    {
        var global = TestConfig.Global();
        global.Home.AdditionalAces = [Ace("SYSTEM", HomeRight.FullControl), Ace("system", HomeRight.Modify)];
        var department = TestConfig.Department();
        department.HomeAdditionalAces = [Ace(@"CC\A", HomeRight.Modify), Ace(@"cc\a", HomeRight.Modify)];

        Assert.Contains(ConfigValidator.ValidateGlobal(global), e => e.Contains("doppelt", StringComparison.Ordinal));
        Assert.Contains(ConfigValidator.ValidateDepartment(department), e => e.Contains("doppelt", StringComparison.Ordinal));
    }

    [Fact]
    public void Logon_server_is_required()
    {
        var global = TestConfig.Global();
        global.LogonScript.Server = "";
        Assert.Contains(ConfigValidator.ValidateGlobal(global), e => e.Contains("Server Anmeldeskripte", StringComparison.Ordinal));
    }
}
