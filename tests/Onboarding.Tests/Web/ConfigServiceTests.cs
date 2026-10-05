using Onboarding.Core.Configuration;
using Onboarding.Data.Seed;
using Onboarding.Web.Services;

namespace Onboarding.Tests.Web;

public sealed class ConfigServiceTests : IDisposable
{
    private readonly WebTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Requester_cannot_change_configuration()
    {
        _host.SignInHr();
        var record = await _host.Config.GetGlobalAsync();

        await Assert.ThrowsAsync<UserFacingException>(() => _host.Config.SaveGlobalAsync(record.Settings, record.Version));
    }

    [Fact]
    public async Task Invalid_global_config_is_rejected()
    {
        _host.SignInAdmin();
        var record = await _host.Config.GetGlobalAsync();
        record.Settings.MailPattern = "{vorname}@cleancontrolling.de";
        record.Settings.ProxyAddressTemplates = ["smtp:{mailLocal}@cleancontrolling.de"];

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => _host.Config.SaveGlobalAsync(record.Settings, record.Version));

        Assert.Contains("Mail-Muster", ex.Message, StringComparison.Ordinal);
        Assert.Contains("primär", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Global_save_with_stale_version_is_a_conflict_and_history_is_written()
    {
        _host.SignInAdmin();
        var first = await _host.Config.GetGlobalAsync();
        var second = await _host.Config.GetGlobalAsync();

        first.Settings.EnableLeadTime = TimeSpan.FromHours(6);
        await _host.Config.SaveGlobalAsync(first.Settings, first.Version);

        second.Settings.EnableLeadTime = TimeSpan.FromHours(2);
        await Assert.ThrowsAsync<UserFacingException>(() => _host.Config.SaveGlobalAsync(second.Settings, second.Version));

        var history = await _host.Config.GetHistoryAsync(null, 10);
        var entry = Assert.Single(history);
        Assert.Equal("it.admin1", entry.ChangedBy);
        Assert.Contains("06:00:00", entry.NewJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Area_requires_existing_ou()
    {
        _host.SignInAdmin();
        var area = (await _host.Config.GetAreasAsync()).Single(a => a.Id == SeedData.AreaBio);
        area.OuDistinguishedName = "OU=Gibtsnicht,DC=CC,DC=local";

        await Assert.ThrowsAsync<UserFacingException>(() => _host.Config.SaveAreaAsync(area));
    }

    [Fact]
    public async Task Department_with_non_ascii_logon_script_is_rejected()
    {
        _host.SignInAdmin();
        var department = new DepartmentConfig { Id = Guid.NewGuid(), Name = "Qualitätssicherung" };
        department.LogonScript.ConnectDrives.Add(new DriveMapping { Letter = "S", Unc = @"\\dc01\{department}" });

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => _host.Config.SaveDepartmentAsync(department));

        Assert.Contains("Nicht-ASCII", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_department_name_is_rejected()
    {
        await _host.CreateDepartmentAsync();

        await Assert.ThrowsAsync<UserFacingException>(() => _host.CreateDepartmentAsync());
    }

    [Fact]
    public async Task Checklist_templates_can_be_reordered()
    {
        _host.SignInAdmin();
        var templates = await _host.Config.GetChecklistTemplatesAsync();
        var second = templates[1];

        await _host.Config.MoveChecklistTemplateAsync(second.Id, -1);

        var reordered = await _host.Config.GetChecklistTemplatesAsync();
        Assert.Equal(second.Id, reordered[0].Id);
        Assert.Equal(templates[0].Id, reordered[1].Id);
    }

    [Fact]
    public void Seed_global_config_is_valid()
    {
        Assert.Empty(ConfigValidator.ValidateGlobal(SeedData.Global()));
    }
}
