using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Onboarding.Core.Configuration;
using Onboarding.Core.Security;
using Onboarding.Steps;
using Onboarding.Steps.Execution;
using Onboarding.Steps.Fakes.World;
using Onboarding.Steps.Scripts;
using Onboarding.Tests.TestSupport;
using static Onboarding.Core.Steps.StepKeys;

namespace Onboarding.Tests.Steps;

/// <summary>Cloud step input, timeout overrides and startup validation (phase 4b).</summary>
public sealed class CloudScriptTests
{
    private const string Password = "Geheim-Start!2026";
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string AppId = "22222222-2222-2222-2222-222222222222";
    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

    private sealed class RecordingRunner : IProcessRunner
    {
        public string Stdin { get; private set; } = "";
        public TimeSpan Timeout { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string standardInput, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Stdin = standardInput;
            Timeout = timeout;
            return Task.FromResult(new ProcessResult(0, """{"status":"done"}""", "", false));
        }
    }

    private static CloudOptions Cloud(params string[] manual) => new()
    {
        TenantId = TenantId,
        AppId = AppId,
        CertificateThumbprint = Thumbprint,
        ExchangeOrganization = "example.onmicrosoft.com",
        ManualSteps = [.. manual],
    };

    private static ScriptOptions Options() => new()
    {
        ScriptsDirectory = "/opt/onboarding/scripts",
        Timeout = TimeSpan.FromMinutes(4),
        TimeoutOverrides = new(StringComparer.Ordinal) { ["Teams."] = TimeSpan.FromMinutes(10), ["Teams.Phone"] = TimeSpan.FromMinutes(12) },
    };

    private static StepContext Context(string? extension, Action<DepartmentConfig>? department = null)
    {
        var request = new RequestFactory().Approved(TestConfig.Person(extension: extension), TestConfig.Snapshot(department: department));
        return new StepContext(request.Id, request.Input, request.Derived!, request.ConfigSnapshot!, false, Guid.NewGuid(),
            () => new SecretString(Password), "S-1-5-21-1-2-3-1105");
    }

    private static async Task<JsonElement> InputFor(string key, StepContext context, CloudOptions cloud, ScriptOptions? options = null)
    {
        var runner = new RecordingRunner();
        var executor = new ScriptStepExecutor(key, IntegrationMode.Real, options ?? Options(), runner, NullLogger<ScriptStepExecutor>.Instance, cloud);
        await executor.ExecuteAsync(context, CancellationToken.None);
        return JsonDocument.Parse(runner.Stdin).RootElement;
    }

    [Fact]
    public async Task Cloud_input_has_auth_identifiers_config_values_and_no_secret_or_paths()
    {
        var input = await InputFor(EntraAssignLicense, Context("12"), Cloud());
        var cloud = input.GetProperty("cloud");

        Assert.Equal(TenantId, cloud.GetProperty("auth").GetProperty("tenantId").GetString());
        Assert.Equal(Thumbprint, cloud.GetProperty("auth").GetProperty("certificateThumbprint").GetString());
        Assert.Equal("example.onmicrosoft.com", cloud.GetProperty("auth").GetProperty("organization").GetString());
        Assert.Equal(["SPB", "MCOEV"], cloud.GetProperty("skus").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("S-1-5-21-1-2-3-1105", input.GetProperty("directoryObjectSid").GetString());
        Assert.False(cloud.GetProperty("manualOnly").GetBoolean());
        Assert.False(input.TryGetProperty("initialPassword", out _));
        Assert.DoesNotContain(Password, input.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("NETLOGON", input.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Phone_skus_only_with_extension()
    {
        var input = await InputFor(EntraAssignLicense, Context(null), Cloud());
        Assert.Equal(["SPB"], input.GetProperty("cloud").GetProperty("skus").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(JsonValueKind.Null, input.GetProperty("cloud").GetProperty("phoneE164").ValueKind);
    }

    [Fact]
    public async Task Teams_and_mailbox_settings_come_from_the_snapshot()
    {
        var context = Context("12", d =>
        {
            d.SharedMailboxes = [new SharedMailboxConfig { Mailbox = "team@example.test", FullAccess = true, AutoMapping = false, SendAs = true }];
            d.Teams.Voicemail = true;
            d.Teams.UnansweredForward = new UnansweredForwardConfig { Enabled = true, Delay = TimeSpan.FromSeconds(20), TargetType = "singleTarget", Target = "hotline@example.test" };
        });

        var cloud = (await InputFor(TeamsForwarding, context, Cloud())).GetProperty("cloud");

        var mailbox = Assert.Single(cloud.GetProperty("sharedMailboxes").EnumerateArray());
        Assert.Equal("team@example.test", mailbox.GetProperty("mailbox").GetString());
        Assert.False(mailbox.GetProperty("autoMapping").GetBoolean());
        var forward = cloud.GetProperty("teams").GetProperty("forward");
        Assert.Equal(20, forward.GetProperty("delaySeconds").GetInt32());
        Assert.Equal("hotline@example.test", forward.GetProperty("target").GetString());
        Assert.Equal("+49746592967812", cloud.GetProperty("phoneE164").GetString());
    }

    [Fact]
    public async Task Manual_steps_are_flagged_and_on_prem_steps_get_no_cloud_block()
    {
        var voicemail = await InputFor(TeamsVoicemail, Context("12"), Cloud(TeamsVoicemail));
        var phone = await InputFor(TeamsPhone, Context("12"), Cloud(TeamsVoicemail));
        var groups = await InputFor(AdGroups, Context("12"), Cloud(TeamsVoicemail));

        Assert.True(voicemail.GetProperty("cloud").GetProperty("manualOnly").GetBoolean());
        Assert.False(phone.GetProperty("cloud").GetProperty("manualOnly").GetBoolean());
        Assert.False(groups.TryGetProperty("cloud", out _));
    }

    [Theory]
    [InlineData(AdCreateUser, 4)]
    [InlineData(EntraWaitUser, 4)]
    [InlineData(TeamsVoicemail, 10)]
    [InlineData(TeamsPhone, 12)]
    public async Task Timeout_overrides_use_the_longest_matching_prefix(string key, int minutes)
    {
        var runner = new RecordingRunner();
        var executor = new ScriptStepExecutor(key, IntegrationMode.Real, Options(), runner, NullLogger<ScriptStepExecutor>.Instance, Cloud());

        await executor.ExecuteAsync(Context("12"), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(minutes), runner.Timeout);
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Integrations:Steps:OnPrem"] = "Real",
        ["Integrations:Steps:Cloud"] = "DryRun",
        ["Integrations:Scripts:ScriptsDirectory"] = "scripts",
        ["Integrations:Scripts:TimeoutOverrides:Teams."] = "00:10:00",
        ["Integrations:Cloud:TenantId"] = TenantId,
        ["Integrations:Cloud:AppId"] = AppId,
        ["Integrations:Cloud:CertificateThumbprint"] = Thumbprint,
        ["Integrations:Cloud:ExchangeOrganization"] = "example.onmicrosoft.com",
        ["Integrations:Cloud:ManualSteps:0"] = TeamsVoicemail,
        ["Worker:ExecutionTimeout"] = "00:12:00",
    };

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStepExecutors(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), "/opt/onboarding");
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Valid_cloud_configuration_registers_cloud_scripts()
    {
        var registry = Build(Valid()).GetRequiredService<StepExecutorRegistry>();

        Assert.Equal(IntegrationMode.DryRun, Assert.IsType<ScriptStepExecutor>(registry.Get(TeamsPhone)).Mode);
        Assert.Equal(IntegrationMode.Real, Assert.IsType<ScriptStepExecutor>(registry.Get(AdCreateUser)).Mode);
        Assert.Equal(21, registry.Keys.Count);
    }

    [Theory]
    [InlineData("Integrations:Cloud:TenantId", "kein-guid")]
    [InlineData("Integrations:Cloud:AppId", "")]
    [InlineData("Integrations:Cloud:CertificateThumbprint", "XYZ")]
    [InlineData("Integrations:Cloud:ExchangeOrganization", "example.com")]
    [InlineData("Integrations:Cloud:ManualSteps:0", "Teams.Phone")]
    [InlineData("Integrations:Scripts:TimeoutOverrides:Telefon.", "00:10:00")]
    [InlineData("Integrations:Scripts:TimeoutOverrides:Teams.", "00:00:00")]
    [InlineData("Worker:ExecutionTimeout", "00:10:00")]
    [InlineData("Integrations:Steps:OnPrem", "Fake")]
    public void Invalid_cloud_configuration_fails_at_startup(string key, string value)
    {
        var values = Valid();
        values[key] = value;

        Assert.Throws<InvalidOperationException>(() => Build(values));
    }

    [Fact]
    public void Fake_world_stays_unchanged_without_scripts()
    {
        var provider = Build(new Dictionary<string, string?>());
        Assert.False(provider.GetRequiredService<FakeWorld>().Options.DetachedFromOnPrem);
        Assert.IsAssignableFrom<FakeStepExecutor>(provider.GetRequiredService<StepExecutorRegistry>().Get(TeamsPhone));
    }
}
