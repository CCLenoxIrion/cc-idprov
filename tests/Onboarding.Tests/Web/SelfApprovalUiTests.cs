using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Onboarding.Core.Security;
using Onboarding.Web.Components.Pages;
using Onboarding.Web.Components.Pages.Requests;
using Onboarding.Web.Services;

namespace Onboarding.Tests.Web;

/// <summary>Badge and filter for self-approved requests (DECISIONS A1).</summary>
public sealed class SelfApprovalUiTests : IDisposable
{
    private const string Password = "Start-Kennwort-2026!x";
    private readonly WebTestHost _host = new();
    private readonly BunitContext _ctx = new();

    public SelfApprovalUiTests()
    {
        _ctx.Services.AddSingleton(_host.Requests);
        _ctx.Services.AddSingleton(_host.Config);
        _ctx.Services.AddSingleton(_host.CurrentUser);
    }

    private async Task<(Guid Self, Guid Other)> TwoApprovedRequestsAsync()
    {
        var departmentId = await _host.CreateDepartmentAsync();
        _host.SignInHr();
        var other = await _host.Requests.CreateAsync(WebTestHost.Input(departmentId), submit: true);
        _host.SignInAdmin();
        var self = await _host.Requests.CreateAsync(WebTestHost.Input(departmentId, "Lisa", "Berg", extension: "13"), submit: true);
        await _host.Requests.ApproveAsync(self, new SecretString(Password), "Einzige IT-Person im Haus");
        await _host.Requests.ApproveAsync(other, new SecretString(Password));
        return (self, other);
    }

    [Fact]
    public async Task Overview_shows_badge_and_filters_self_approvals()
    {
        var (self, other) = await TwoApprovedRequestsAsync();

        var cut = _ctx.Render<Home>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=overview] tbody tr").Count));
        var badges = cut.FindAll("[data-testid=self-approved-badge]");
        var badgeRow = Assert.Single(badges).Closest("tr")!;
        Assert.Contains($"requests/{self}", badgeRow.InnerHtml, StringComparison.Ordinal);

        cut.Find("[data-testid=filter-self-approved]").Change(true);

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=overview] tbody tr");
            Assert.Single(rows);
            Assert.DoesNotContain($"requests/{other}", rows[0].InnerHtml, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Detail_shows_badge_and_reason()
    {
        var (self, other) = await TwoApprovedRequestsAsync();

        var selfPage = _ctx.Render<RequestDetail>(p => p.Add(c => c.Id, self));
        selfPage.WaitForAssertion(() => selfPage.Find("[data-testid=self-approved-badge]"));
        Assert.Contains("Einzige IT-Person im Haus", selfPage.Markup, StringComparison.Ordinal);

        var otherPage = _ctx.Render<RequestDetail>(p => p.Add(c => c.Id, other));
        otherPage.WaitForAssertion(() => Assert.Contains("freigegeben von", otherPage.Markup, StringComparison.Ordinal));
        Assert.Empty(otherPage.FindAll("[data-testid=self-approved-badge]"));
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _host.Dispose();
    }
}
