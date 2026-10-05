using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Operations;
using Onboarding.Steps.Security;
using Onboarding.Tests.Web;
using Onboarding.Web.Components.Common;
using Onboarding.Web.Services;
using Onboarding.Worker;

namespace Onboarding.Tests.Worker;

/// <summary>Certificate expiry warnings (SPEC §9, DECISIONS X15).</summary>
public sealed class CertificateWarningTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private readonly WebTestHost _host = new();

    private sealed class FakeCertificates(Dictionary<string, DateTimeOffset> known) : ICertificateInfoSource
    {
        public CertificateInfo? Find(string thumbprint) =>
            known.TryGetValue(thumbprint, out var notAfter) ? new CertificateInfo(notAfter) : null;
    }

    private static CertificateStatus Status(string name, DateTimeOffset? notAfter, bool found = true, DateTimeOffset? checkedAt = null) =>
        new() { Name = name, Thumbprint = "ABCDEF0123456789", Found = found, NotAfter = notAfter, CheckedAt = checkedAt ?? Now };

    [Fact]
    public void Valid_certificate_far_from_expiry_gives_no_warning() =>
        Assert.Empty(CertificateWarnings.Evaluate([Status(CertificateNames.CloudApp, Now.AddDays(200))], Now, 30, TimeSpan.FromHours(48), Zone));

    [Theory]
    [InlineData(29, false)]
    [InlineData(7, true)]
    public void Expiring_within_warning_period_is_reported(int days, bool critical)
    {
        var warning = Assert.Single(CertificateWarnings.Evaluate([Status(CertificateNames.CloudApp, Now.AddDays(days))], Now, 30, TimeSpan.FromHours(48), Zone));
        Assert.Contains($"in {days} Tagen", warning.Message, StringComparison.Ordinal);
        Assert.Equal(critical, warning.Critical);
    }

    [Fact]
    public void Expired_missing_and_stale_are_reported()
    {
        var warnings = CertificateWarnings.Evaluate(
        [
            Status(CertificateNames.PasswordCertificate, Now.AddDays(-1)),
            Status(CertificateNames.CloudApp, null, found: false),
            Status(CertificateNames.GraphRead, Now.AddDays(300), checkedAt: Now.AddDays(-3)),
        ], Now, 30, TimeSpan.FromHours(48), Zone);

        Assert.Contains(warnings, w => w.Critical && w.Message.Contains("abgelaufen", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Critical && w.Message.Contains("nicht im Zertifikatsspeicher", StringComparison.Ordinal));
        Assert.Contains(warnings, w => !w.Critical && w.Message.Contains("läuft der Worker", StringComparison.Ordinal));
    }

    private CertificateMonitor Monitor(Dictionary<string, string?> settings, ICertificateInfoSource certificates) =>
        new(_host.DbFactory, certificates, new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), _host.Time,
            NullLogger<CertificateMonitor>.Instance);

    [Fact]
    public async Task Monitor_stores_configured_worker_certificates_and_drops_unconfigured_ones()
    {
        // The test host's DbContext needs a resolved actor (the worker host uses "worker").
        _host.SignInAdmin();
        await _host.CurrentUser.GetAsync();
        await using (var db = _host.DbFactory.CreateDbContext())
        {
            var global = await db.GlobalConfig.SingleAsync();
            global.Settings.PasswordCertThumbprint = "PWDCERT";
            db.Entry(global).Property(g => g.Settings).IsModified = true;
            await db.SaveChangesAsync();
        }

        var certificates = new FakeCertificates(new() { ["PWDCERT"] = Now.AddDays(100) });
        await Monitor(new()
        {
            ["Integrations:Steps:OnPrem"] = "Real",
            ["Integrations:Steps:Cloud"] = "Real",
            ["Integrations:Cloud:CertificateThumbprint"] = "CLOUDCERT",
        }, certificates).CheckOnceAsync(default);

        await using (var db = _host.DbFactory.CreateDbContext())
        {
            var rows = await db.CertificateStatuses.OrderBy(c => c.Name).ToListAsync();
            Assert.Equal([CertificateNames.CloudApp, CertificateNames.PasswordCertificate], rows.Select(r => r.Name));
            Assert.False(rows[0].Found);
            Assert.Equal(Now.AddDays(100), rows[1].NotAfter);
        }

        // Cloud back to Fake, development key: nothing left to check.
        await Monitor(new() { ["SecretProtection:Mode"] = "DevelopmentPem" }, certificates).CheckOnceAsync(default);
        await using (var db = _host.DbFactory.CreateDbContext())
        {
            Assert.Empty(await db.CertificateStatuses.ToListAsync());
        }
    }

    [Fact]
    public async Task Banner_shows_warnings_including_the_web_graph_certificate()
    {
        _host.SignInAdmin();
        await _host.CurrentUser.GetAsync();
        await using (var db = _host.DbFactory.CreateDbContext())
        {
            db.CertificateStatuses.Add(Status(CertificateNames.CloudApp, _host.Time.GetUtcNow().AddDays(10), checkedAt: _host.Time.GetUtcNow()));
            await db.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:Read:Graph"] = "Real",
            ["Integrations:Read:GraphAuth:CertificateThumbprint"] = "WEBCERT",
        }).Build();
        var service = new CertificateStatusService(_host.DbFactory, new FakeCertificates([]), configuration, _host.Time);

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton(service);
        var cut = ctx.Render<CertificateBanner>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=certificate-banner]"));
        Assert.Contains("Cloud-App-Registrierung", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("in 10 Tagen", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Graph-Lese-App", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("alert-danger", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_banner_without_warnings()
    {
        var service = new CertificateStatusService(_host.DbFactory, new FakeCertificates([]), new ConfigurationBuilder().Build(), _host.Time);
        Assert.Empty(await service.GetWarningsAsync());
    }

    public void Dispose() => _host.Dispose();
}
