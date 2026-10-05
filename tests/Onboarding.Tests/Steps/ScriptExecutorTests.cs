using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Onboarding.Core.Security;
using Onboarding.Core.Steps;
using Onboarding.Steps;
using Onboarding.Steps.Execution;
using Onboarding.Steps.Fakes.World;
using Onboarding.Steps.Scripts;
using Onboarding.Tests.TestSupport;
using static Onboarding.Core.Steps.StepKeys;

namespace Onboarding.Tests.Steps;

public sealed class ScriptExecutorTests
{
    private const string Password = "Geheim-Start!2026";

    private sealed class RecordingRunner(ProcessResult result) : IProcessRunner
    {
        public string? FileName { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string Stdin { get; private set; } = "";

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string standardInput, TimeSpan timeout, CancellationToken cancellationToken)
        {
            FileName = fileName;
            Arguments = arguments;
            Stdin = standardInput;
            return Task.FromResult(result);
        }
    }

    private static readonly ScriptOptions Options = new() { PwshPath = "pwsh", ScriptsDirectory = "/opt/onboarding/scripts", Timeout = TimeSpan.FromMinutes(1) };

    private static StepContext Context(bool force = false)
    {
        var request = new RequestFactory().Approved(TestConfig.Person(extension: "12", doctor: true));
        return new StepContext(request.Id, request.Input, request.Derived!, request.ConfigSnapshot!, force, null, () => new SecretString(Password));
    }

    private static (ScriptStepExecutor Executor, RecordingRunner Runner) Create(string key, IntegrationMode mode, string stdout, int exitCode = 0, string stderr = "", bool timedOut = false)
    {
        var runner = new RecordingRunner(new ProcessResult(exitCode, stdout, stderr, timedOut));
        return (new ScriptStepExecutor(key, mode, Options, runner, NullLogger<ScriptStepExecutor>.Instance), runner);
    }

    [Fact]
    public async Task Create_user_input_contains_password_only_via_stdin_and_no_paths()
    {
        var (executor, runner) = Create(AdCreateUser, IntegrationMode.Real, """{"status":"done","directoryObjectGuid":"11111111-2222-3333-4444-555555555555"}""");

        var outcome = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(StepOutcomeKind.Done, outcome.Kind);
        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), outcome.DirectoryObjectGuid);
        Assert.Equal("pwsh", runner.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-File", Path.Combine("/opt/onboarding/scripts", "steps", "AD.CreateUser.ps1")], runner.Arguments);
        Assert.DoesNotContain(runner.Arguments, a => a.Contains(Password, StringComparison.Ordinal));

        using var json = JsonDocument.Parse(runner.Stdin);
        var root = json.RootElement;
        Assert.Equal(Password, root.GetProperty("initialPassword").GetString());
        Assert.False(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal("lirion", root.GetProperty("identity").GetProperty("sam").GetString());
        Assert.Equal("Dr.", root.GetProperty("identity").GetProperty("additionalAttributes").GetProperty("extensionAttribute1").GetString());
        Assert.Equal("extensionAttribute15", root.GetProperty("config").GetProperty("requestIdAttribute").GetString());
        Assert.Equal("DC01", root.GetProperty("config").GetProperty("jea").GetProperty("dcComputer").GetString());
        // No file system locations in the contract (only the JEA endpoint knows them).
        Assert.DoesNotContain("NETLOGON", runner.Stdin, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"F:\\Home", runner.Stdin, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("folderUnc", runner.Stdin, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AdGroups)]
    [InlineData(LogonScript)]
    [InlineData(SyncDelta)]
    [InlineData(AdEnable)]
    public async Task Other_steps_get_no_password(string key)
    {
        var (executor, runner) = Create(key, IntegrationMode.Real, """{"status":"done"}""");

        await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.DoesNotContain("initialPassword", runner.Stdin, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, runner.Stdin, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logon_script_bytes_are_those_of_the_generator()
    {
        var context = Context(force: true);
        var (executor, runner) = Create(LogonScript, IntegrationMode.Real, """{"status":"done"}""");

        await executor.ExecuteAsync(context, CancellationToken.None);

        using var json = JsonDocument.Parse(runner.Stdin);
        var logon = json.RootElement.GetProperty("logonScript");
        var expected = LogonScriptFile.For(context);
        Assert.Equal(expected.Content, Convert.FromBase64String(logon.GetProperty("contentBase64").GetString()!));
        Assert.Equal(expected.Sha256, logon.GetProperty("sha256").GetString());
        Assert.True(json.RootElement.GetProperty("force").GetBoolean());
    }

    [Theory]
    [InlineData("""{"status":"done","reason":"ok"}""", StepOutcomeKind.Done)]
    [InlineData("""{"status":"waiting","reason":"busy"}""", StepOutcomeKind.Waiting)]
    [InlineData("""{"status":"failed","reason":"Gruppe fehlt"}""", StepOutcomeKind.Failed)]
    [InlineData("""{"status":"needsInput","reason":"fremdes Konto"}""", StepOutcomeKind.NeedsInput)]
    [InlineData("""{"status":"manualTask","reason":"bitte manuell"}""", StepOutcomeKind.ManualTask)]
    [InlineData("""{"status":"skipped","reason":"nichts zu tun"}""", StepOutcomeKind.Skipped)]
    [InlineData("""{"status":"exploded"}""", StepOutcomeKind.Failed)]
    [InlineData("kein json", StepOutcomeKind.Failed)]
    [InlineData("", StepOutcomeKind.Failed)]
    public void Maps_status(string stdout, StepOutcomeKind expected)
    {
        var (executor, _) = Create(AdGroups, IntegrationMode.Real, stdout);

        Assert.Equal(expected, executor.Map(stdout).Kind);
    }

    [Theory]
    [InlineData("""{"status":"needsInput","code":"ou-mismatch","reason":"andere OU"}""", "ou-mismatch")]
    [InlineData("""{"status":"failed","code":"invalid-sam","reason":"x"}""", "invalid-sam")]
    [InlineData("""{"status":"failed","reason":"ohne Code"}""", "unspecified")]
    [InlineData("""{"status":"failed","code":"Freitext mit Leerzeichen","reason":"x"}""", "invalid-code")]
    [InlineData("""{"status":"exploded","code":"ou-mismatch"}""", "invalid-output")]
    [InlineData("kein json", "invalid-output")]
    [InlineData("""{"status":"done","code":"ignored"}""", null)]
    public void Maps_reason_code(string stdout, string? expected)
    {
        var (executor, _) = Create(AdCreateUser, IntegrationMode.Real, stdout);

        Assert.Equal(expected, executor.Map(stdout).Code);
    }

    [Theory]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1105", "S-1-5-21-1111111111-2222222222-3333333333-1105")]
    [InlineData("S-1-5-18", null)]
    [InlineData("<script>", null)]
    public void Maps_directory_object_sid(string sid, string? expected)
    {
        var stdout = "{\"status\":\"done\",\"directoryObjectGuid\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"directoryObjectSid\":\"" + sid + "\"}";
        var (executor, _) = Create(AdCreateUser, IntegrationMode.Real, stdout);

        Assert.Equal(expected, executor.Map(stdout).DirectoryObjectSid);
    }

    [Fact]
    public async Task Executor_failures_have_fixed_codes()
    {
        var (exit, _) = Create(AdEnable, IntegrationMode.Real, "", exitCode: 1);
        var (timeout, _) = Create(AdEnable, IntegrationMode.Real, "", exitCode: -1, timedOut: true);
        var (dryRun, _) = Create(AdEnable, IntegrationMode.DryRun, """{"status":"done","dryRun":true,"plannedActions":["Konto aktivieren"]}""");

        Assert.Equal("script-exit-code", (await exit.ExecuteAsync(Context(), CancellationToken.None)).Code);
        Assert.Equal("script-timeout", (await timeout.ExecuteAsync(Context(), CancellationToken.None)).Code);
        Assert.Equal("dry-run", (await dryRun.ExecuteAsync(Context(), CancellationToken.None)).Code);
    }

    [Fact]
    public void Tolerates_stray_lines_before_the_json()
    {
        var (executor, _) = Create(AdGroups, IntegrationMode.Real, "");

        var outcome = executor.Map("WARNING: irgendwas\n{\"status\":\"done\",\"output\":{\"groups\":[\"GG-Vertrieb\"]}}\n");

        Assert.Equal(StepOutcomeKind.Done, outcome.Kind);
        Assert.Contains("GG-Vertrieb", outcome.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Dry_run_with_planned_actions_needs_input_and_without_is_done()
    {
        var (executor, _) = Create(AdCreateUser, IntegrationMode.DryRun, "");

        var planned = executor.Map("""{"status":"done","dryRun":true,"plannedActions":["Konto lirion anlegen in OU=Users","Manager setzen"]}""");
        var nothing = executor.Map("""{"status":"done","dryRun":true,"plannedActions":[]}""");

        Assert.Equal(StepOutcomeKind.NeedsInput, planned.Kind);
        Assert.Equal("Dry-Run – würde: Konto lirion anlegen in OU=Users; Manager setzen", planned.Message);
        Assert.Equal(StepOutcomeKind.Done, nothing.Kind);
    }

    [Fact]
    public async Task Dry_run_flag_is_sent()
    {
        var (executor, runner) = Create(AdEnable, IntegrationMode.DryRun, """{"status":"done","dryRun":true}""");

        await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.True(JsonDocument.Parse(runner.Stdin).RootElement.GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task Exit_code_and_stderr_never_reach_the_outcome()
    {
        var (executor, _) = Create(AdCreateUser, IntegrationMode.Real, "", exitCode: 1,
            stderr: $"New-ADUser : Das Kennwort {Password} erfüllt nicht ... at line 12");

        var outcome = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(StepOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("Exit-Code 1", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("New-ADUser", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_echoed_by_a_faulty_script_is_redacted()
    {
        var stdout = "{\"status\":\"failed\",\"reason\":\"Fehler mit " + Password + "\",\"output\":{\"x\":\"" + Password + "\"}}";
        var (executor, _) = Create(AdCreateUser, IntegrationMode.Real, stdout);

        var outcome = await executor.ExecuteAsync(Context(), CancellationToken.None);

        Assert.DoesNotContain(Password, outcome.Message, StringComparison.Ordinal);
        Assert.Contains("***", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_is_waiting()
    {
        var (executor, _) = Create(SyncDelta, IntegrationMode.Real, "", exitCode: -1, timedOut: true);

        Assert.Equal(StepOutcomeKind.Waiting, (await executor.ExecuteAsync(Context(), CancellationToken.None)).Kind);
    }

    [Fact]
    public void Script_executor_rejects_fake_mode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScriptStepExecutor(AdEnable, IntegrationMode.Fake, Options, new RecordingRunner(new(0, "", "", false)), NullLogger<ScriptStepExecutor>.Instance));
    }

    [Fact]
    public void Group_switch_uses_scripts_for_on_prem_only()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Integrations:Steps:OnPrem"] = "DryRun",
            ["Integrations:Steps:Cloud"] = "Fake",
            ["Integrations:Scripts:ScriptsDirectory"] = "scripts",
        });

        var registry = provider.GetRequiredService<StepExecutorRegistry>();

        foreach (var key in StepGroups.OnPremKeys)
        {
            var executor = Assert.IsType<ScriptStepExecutor>(registry.Get(key));
            Assert.Equal(IntegrationMode.DryRun, executor.Mode);
        }

        Assert.IsAssignableFrom<FakeStepExecutor>(registry.Get(EntraWaitUser));
        Assert.True(provider.GetRequiredService<FakeWorld>().Options.DetachedFromOnPrem);
        Assert.Equal(21, registry.Keys.Count);
    }

    [Theory]
    [InlineData("Real", "Real")] // cloud without Integrations:Cloud
    [InlineData("Fake", "DryRun")] // cloud real with fake AD
    [InlineData("Banane", "Fake")]
    public void Invalid_modes_fail_at_startup(string onPrem, string cloud)
    {
        Assert.ThrowsAny<Exception>(() => Build(new Dictionary<string, string?>
        {
            ["Integrations:Steps:OnPrem"] = onPrem,
            ["Integrations:Steps:Cloud"] = cloud,
            ["Integrations:Scripts:ScriptsDirectory"] = "scripts",
        }).GetRequiredService<StepExecutorRegistry>());
    }

    [Fact]
    public void Real_mode_requires_scripts_directory()
    {
        Assert.Throws<InvalidOperationException>(() => Build(new Dictionary<string, string?> { ["Integrations:Steps:OnPrem"] = "Real" }));
    }

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStepExecutors(configuration, "/opt/onboarding");
        return services.BuildServiceProvider();
    }
}

/// <summary>Process runner against /bin/sh – no pwsh involved.</summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Passes_stdin_and_captures_stdout_and_stderr()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await new ProcessRunner().RunAsync("/bin/sh", ["-c", "cat; echo fehler >&2; exit 3"],
            """{"status":"done","ä":"ü"}""", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("""{"status":"done","ä":"ü"}""", result.StandardOutput);
        Assert.Equal("fehler", result.StandardError.Trim());
    }

    [Fact]
    public async Task Timeout_kills_the_process_tree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var started = DateTime.UtcNow;
        var result = await new ProcessRunner().RunAsync("/bin/sh", ["-c", "sleep 30 & wait"], "", TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Process_that_ignores_stdin_is_not_a_timeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var big = new string('x', 1_000_000);
        var result = await new ProcessRunner().RunAsync("/bin/sh", ["-c", "exit 0"], big, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result.StandardOutput)));
    }
}
