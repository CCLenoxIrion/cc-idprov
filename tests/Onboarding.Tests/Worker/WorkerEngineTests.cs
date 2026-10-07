using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Domain;
using Onboarding.Core.Steps;
using Onboarding.Steps.Execution;
using Onboarding.Steps.Fakes.World;
using Onboarding.Worker;
using static Onboarding.Core.Steps.StepKeys;

namespace Onboarding.Tests.Worker;

public sealed class WorkerEngineTests
{
    private static readonly TimeSpan[] ExpectedBackoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)];

    private static bool AllStepsFinished(Request r) =>
        r.Steps.All(s => s.Status is StepStatus.Done or StepStatus.Skipped);

    private static string Describe(Request r) =>
        string.Join(", ", r.Steps.Where(s => s.Status != StepStatus.Done).Select(s => $"{s.StepKey}:{s.Status}"));

    [Fact]
    public async Task Single_request_runs_to_awaiting_checklist_and_completes_after_checklist() // AK 1, AK 7
    {
        using var host = new WorkerTestHost();
        var id = await host.CreateApprovedAsync();

        await host.PollAsync(12);

        var request = await host.LoadAsync(id);
        Assert.True(AllStepsFinished(request), Describe(request));
        Assert.Equal(RequestStatus.AwaitingChecklist, request.Status);
        Assert.NotNull(request.DirectoryObjectGuid);
        Assert.Equal(request.DirectoryObjectGuid, host.World.AdUsers["lirion"].ObjectGuid);
        Assert.Equal(id.ToString(), host.World.AdUsers["lirion"].Attributes["extensionAttribute15"]);
        Assert.False(host.World.AdUsers["lirion"].Attributes.ContainsKey("extensionAttribute1")); // no title
        Assert.True(host.World.AdUsers["lirion"].Enabled);
        Assert.True(host.World.CloudUsers["l.irion@cleancontrolling.de"].AccountEnabled);
        Assert.Null(request.EncryptedInitialPassword); // P4

        await host.TickAllMandatoryAsync(id);
        await host.PollAsync(1);

        Assert.Equal(RequestStatus.Completed, (await host.LoadAsync(id)).Status);
    }

    [Fact]
    public async Task Logon_script_hash_is_in_step_output_and_audit()
    {
        using var host = new WorkerTestHost();
        var id = await host.CreateApprovedAsync();

        await host.PollAsync(3);

        var step = (await host.LoadAsync(id)).FindStep(LogonScript)!;
        Assert.Equal(StepStatus.Done, step.Status);
        var output = JsonDocument.Parse(step.OutputJson!).RootElement;
        var sha = output.GetProperty("Sha256").GetString()!;
        var path = output.GetProperty("Path").GetString()!;
        Assert.Equal(64, sha.Length);
        Assert.Equal(sha, LogonScriptFile.Sha256Of(host.World.Files[path]));
        Assert.Contains(await host.AuditAsync(id), a => a.StepKey == LogonScript && (a.Details ?? "").Contains(sha, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Three_parallel_requests_one_fails_without_affecting_the_others() // AK 6
    {
        var world = WorkerTestHost.DefaultWorld();
        world.Faults.Add(new FakeFault { StepKey = TeamsPhone, Sam = "pfehler", Mode = FakeFaultMode.Fail, Count = 1, Message = "Nummer gesperrt (simuliert)." });
        using var host = new WorkerTestHost(world);
        var a = await host.CreateApprovedAsync("Anna", "Alpha", "21", entryInDays: 2);
        var b = await host.CreateApprovedAsync("Bernd", "Beta", "22", entryInDays: 2);
        var faulty = await host.CreateApprovedAsync("Paul", "Fehler", "23", entryInDays: 2);

        // Run until well after the entry date (00:00 Berlin in two days).
        await host.PollAsync(20, TimeSpan.FromHours(3));

        var failed = await host.LoadAsync(faulty);
        Assert.Equal(RequestStatus.Failed, failed.Status);
        Assert.Equal(StepStatus.Failed, failed.FindStep(TeamsPhone)!.Status);
        Assert.Equal("Nummer gesperrt (simuliert).", failed.FindStep(TeamsPhone)!.LastError);
        // Dependent steps are blocked, independent ones continued.
        Assert.Equal(StepStatus.Pending, failed.FindStep(TeamsVoiceRouting)!.Status);
        Assert.Equal(StepStatus.Done, failed.FindStep(EntraWaitEnabled)!.Status);
        Assert.Equal(StepStatus.Done, failed.FindStep(ExoSharedMailboxes)!.Status);
        // Progress as shown in the overview: Teams.Phone failed, its three dependents pending.
        Assert.Equal(17, failed.Steps.Count(s => s.Status is StepStatus.Done or StepStatus.Skipped));

        foreach (var ok in new[] { a, b })
        {
            var request = await host.LoadAsync(ok);
            Assert.True(AllStepsFinished(request), Describe(request));
            Assert.Equal(RequestStatus.AwaitingChecklist, request.Status);
        }

        Assert.Equal(1, host.Executions.MaxConcurrentPerRequest);

        // Admin retries; the fault fired only once, so the request continues.
        await host.MutateAsync(faulty, r => host.Workflow.RetryStep(r, TeamsPhone, WorkerTestHost.Admin));
        await host.PollAsync(4);
        failed = await host.LoadAsync(faulty);
        Assert.True(AllStepsFinished(failed), Describe(failed));
        Assert.Equal(RequestStatus.AwaitingChecklist, failed.Status);
    }

    [Fact]
    public async Task Waiting_step_does_not_block_other_due_steps_of_the_same_request() // P1
    {
        var world = WorkerTestHost.DefaultWorld();
        world.TeamsUserDelayChecks = 1000;
        using var host = new WorkerTestHost(world);
        var id = await host.CreateApprovedAsync(extension: "12");

        await host.PollAsync(15);

        var request = await host.LoadAsync(id);
        Assert.Equal(StepStatus.Waiting, request.FindStep(TeamsWaitUser)!.Status);
        Assert.Equal(StepStatus.Done, request.FindStep(ExoDisableNewOutlook)!.Status);
        Assert.Equal(StepStatus.Done, request.FindStep(ExoSharedMailboxes)!.Status);
        Assert.Equal(StepStatus.Done, request.FindStep(EntraWaitEnabled)!.Status);
        Assert.Equal(RequestStatus.Waiting, request.Status);
        Assert.Equal(1, host.Executions.MaxConcurrentPerRequest);
    }

    [Fact]
    public async Task Approval_14_days_before_entry_does_not_time_out() // P2
    {
        using var host = new WorkerTestHost();
        var id = await host.CreateApprovedAsync(entryInDays: 14);

        // 13 days in 6-hour polls: everything up to AD.Enable is done, AD.Enable untouched.
        await host.PollAsync(13 * 4, TimeSpan.FromHours(6));

        var request = await host.LoadAsync(id);
        var enable = request.FindStep(AdEnable)!;
        Assert.Equal(StepStatus.Pending, enable.Status);
        Assert.Equal(0, enable.Attempts);
        Assert.Null(enable.FirstAttemptAt);
        Assert.Equal(0, request.FindStep(SyncDeltaAfterEnable)!.Attempts);
        Assert.DoesNotContain(request.Steps, s => s.Status == StepStatus.Failed);
        Assert.NotEqual(RequestStatus.Failed, request.Status);
        Assert.Equal(StepStatus.Done, request.FindStep(TeamsForwarding)!.Status);

        // Entry day: enable, sync, wait for accountEnabled.
        await host.PollAsync(8, TimeSpan.FromHours(6));

        request = await host.LoadAsync(id);
        Assert.True(AllStepsFinished(request), Describe(request));
        Assert.Equal(RequestStatus.AwaitingChecklist, request.Status);
    }

    [Fact]
    public async Task Waiting_is_audited_only_on_status_changes() // P3
    {
        var world = WorkerTestHost.DefaultWorld();
        world.TeamsUserDelayChecks = 20;
        using var host = new WorkerTestHost(world);
        var id = await host.CreateApprovedAsync();

        await host.PollAsync(40);

        var request = await host.LoadAsync(id);
        var step = request.FindStep(TeamsWaitUser)!;
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(21, step.Attempts);

        var audits = (await host.AuditAsync(id)).Where(a => a.StepKey == TeamsWaitUser).ToList();
        Assert.Equal([AuditActions.StepStarted, AuditActions.StepWaiting, AuditActions.StepFinished], audits.Select(a => a.Action));
        Assert.Contains("Versuche: 21", audits[^1].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waiting_step_times_out_after_configured_step_timeout()
    {
        var world = WorkerTestHost.DefaultWorld();
        world.EntraUserDelayChecks = 100_000;
        using var host = new WorkerTestHost(world);
        var id = await host.CreateApprovedAsync(configure: s => s.Global.Execution.StepTimeout = TimeSpan.FromHours(2));

        await host.PollAsync(20);

        var request = await host.LoadAsync(id);
        var step = request.FindStep(EntraWaitUser)!;
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.StartsWith("Timeout", step.LastError, StringComparison.Ordinal);
        Assert.Equal(RequestStatus.Failed, request.Status);
    }

    [Fact]
    public async Task Backoff_follows_configured_schedule()
    {
        var world = WorkerTestHost.DefaultWorld();
        world.EntraUserDelayChecks = 100;
        using var host = new WorkerTestHost(world);
        var id = await host.CreateApprovedAsync();
        await host.Engine.RunOnceAsync(CancellationToken.None);

        var delays = new List<TimeSpan>();
        for (var i = 0; i < 6; i++)
        {
            var step = (await host.LoadAsync(id)).FindStep(EntraWaitUser)!;
            var wait = step.NextAttemptAt!.Value - host.Time.GetUtcNow();
            delays.Add(wait);
            host.Time.Advance(wait);
            await host.Engine.RunOnceAsync(CancellationToken.None);
        }

        Assert.Equal(ExpectedBackoff, delays);
    }

    [Fact]
    public async Task Initial_password_lifetime() // P4
    {
        var world = WorkerTestHost.DefaultWorld();
        world.ExistingSamAccountNames.Add("lirion"); // foreign account → AD.CreateUser NeedsInput (AK 4)
        using var host = new WorkerTestHost(world);
        var conflict = await host.CreateApprovedAsync();
        var cancelled = await host.CreateApprovedAsync("Clara", "Cancel", "44");

        await host.PollAsync(2);

        var request = await host.LoadAsync(conflict);
        Assert.Equal(StepStatus.NeedsInput, request.FindStep(AdCreateUser)!.Status);
        Assert.Equal(RequestStatus.NeedsInput, request.Status);
        Assert.NotNull(request.EncryptedInitialPassword); // kept while AD.CreateUser needs input
        Assert.Null(request.DirectoryObjectGuid);
        Assert.Contains("gehört nicht zu diesem Auftrag", request.FindStep(AdCreateUser)!.LastError, StringComparison.Ordinal);

        await host.MutateAsync(cancelled, r => host.Workflow.Cancel(r, WorkerTestHost.Admin, "Test"));
        Assert.Null((await host.LoadAsync(cancelled)).EncryptedInitialPassword);

        await using var db = host.DbFactory.CreateDbContext();
        Assert.Equal(1, await db.Requests.CountAsync(r => r.EncryptedInitialPassword != null));
    }

    [Fact]
    public async Task Identity_conflict_at_create_user_is_resolved_by_admin_and_reruns()
    {
        var world = WorkerTestHost.DefaultWorld();
        world.ExistingSamAccountNames.Add("lirion");
        using var host = new WorkerTestHost(world);
        var id = await host.CreateApprovedAsync();
        await host.PollAsync(2);

        var identityOverride = new IdentityOverride("lirion2", "l.irion@cleancontrolling.de");
        await host.MutateAsync(id, r => host.Workflow.ResolveNeedsInput(
            r, WorkerTestHost.Admin, identityOverride, Onboarding.Tests.TestSupport.RequestFactory.Check(r.Input, identityOverride)));
        await host.PollAsync(12);

        var request = await host.LoadAsync(id);
        Assert.True(AllStepsFinished(request), Describe(request));
        Assert.Equal(request.DirectoryObjectGuid, host.World.AdUsers["lirion2"].ObjectGuid);
        Assert.Null(request.EncryptedInitialPassword);
    }

    [Fact]
    public async Task Changed_logon_script_needs_input_and_is_overwritten_only_on_force() // L3
    {
        using var host = new WorkerTestHost();
        var id = await host.CreateApprovedAsync();
        host.World.Files[@"\\dc03\NETLOGON\lirion.bat"] = "manuell\r\n"u8.ToArray();

        await host.PollAsync(5);
        var step = (await host.LoadAsync(id)).FindStep(LogonScript)!;
        Assert.Equal(StepStatus.NeedsInput, step.Status);
        Assert.Contains("manuell angelegt oder geändert", step.LastError, StringComparison.Ordinal);
        Assert.Equal("manuell\r\n"u8.ToArray(), host.World.Files[@"\\dc03\NETLOGON\lirion.bat"]);
        Assert.Equal(StepStatus.Pending, (await host.LoadAsync(id)).FindStep(SyncDelta)!.Status); // blocked

        // Retry without force keeps the file.
        await host.MutateAsync(id, r => host.Workflow.RetryStep(r, LogonScript, WorkerTestHost.Admin));
        await host.PollAsync(1);
        Assert.Equal(StepStatus.NeedsInput, (await host.LoadAsync(id)).FindStep(LogonScript)!.Status);

        await host.MutateAsync(id, r => host.Workflow.ForceStep(r, LogonScript, WorkerTestHost.Admin, "Altes Skript ersetzen"));
        await host.PollAsync(1);
        step = (await host.LoadAsync(id)).FindStep(LogonScript)!;
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.False(step.ForceRequested);
        Assert.StartsWith("net use", System.Text.Encoding.ASCII.GetString(host.World.Files[@"\\dc03\NETLOGON\lirion.bat"]), StringComparison.Ordinal);
        Assert.Contains(await host.AuditAsync(id), a => a.Action == AuditActions.StepForced && a.Details == "Altes Skript ersetzen");
    }

    [Fact]
    public async Task Two_engines_never_execute_the_same_step_twice()
    {
        using var host = new WorkerTestHost();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await host.CreateApprovedAsync("Max", $"Muster{(char)('a' + i)}", $"3{i}"));
        }

        var second = host.CreateEngine();
        for (var poll = 0; poll < 15; poll++)
        {
            await Task.WhenAll(host.Engine.RunOnceAsync(CancellationToken.None), second.RunOnceAsync(CancellationToken.None));
            host.Time.Advance(TimeSpan.FromMinutes(15));
        }

        foreach (var id in ids)
        {
            var request = await host.LoadAsync(id);
            Assert.True(AllStepsFinished(request), Describe(request));
            foreach (var step in request.Steps.Where(s => s.Status == StepStatus.Done))
            {
                Assert.Equal(step.Attempts, host.Executions.Count(id, step.StepKey));
            }
        }

        Assert.Equal(1, host.Executions.MaxConcurrentPerRequest);
    }

    [Fact]
    public async Task Interrupted_step_is_recovered_and_rerun()
    {
        using var host = new WorkerTestHost();
        var id = await host.CreateApprovedAsync();
        await host.MutateAsync(id, r => host.Workflow.ClaimStep(r, r.FindStep(AdCreateUser)!)); // crash after claim

        await host.Engine.RecoverAsync(CancellationToken.None);
        var request = await host.LoadAsync(id);
        Assert.Equal(StepStatus.Waiting, request.FindStep(AdCreateUser)!.Status);
        Assert.Contains(await host.AuditAsync(id), a => a.Action == AuditActions.StepRecovered);

        await host.PollAsync(12);
        Assert.True(AllStepsFinished(await host.LoadAsync(id)));
    }

    [Fact]
    public async Task Hanging_or_throwing_step_does_not_block_other_requests()
    {
        var world = WorkerTestHost.DefaultWorld();
        world.Faults.Add(new FakeFault { StepKey = HomeFolder, Sam = "hhang", Mode = FakeFaultMode.Hang, Count = 1 });
        world.Faults.Add(new FakeFault { StepKey = HomeFolder, Sam = "tthrow", Mode = FakeFaultMode.Throw, Message = "Boom (simuliert)" });
        using var host = new WorkerTestHost(world, new WorkerOptions { MaxParallelRequests = 3, ExecutionTimeout = TimeSpan.FromMilliseconds(300) });
        var hang = await host.CreateApprovedAsync("Hans", "Hang", "41");
        var thrower = await host.CreateApprovedAsync("Tina", "Throw", "42");
        var ok = await host.CreateApprovedAsync(extension: "43");

        await host.Engine.RunOnceAsync(CancellationToken.None);

        var hanging = (await host.LoadAsync(hang)).FindStep(HomeFolder)!;
        Assert.Equal(StepStatus.Waiting, hanging.Status);
        Assert.Contains("abgebrochen", hanging.Note, StringComparison.Ordinal);
        var thrown = (await host.LoadAsync(thrower)).FindStep(HomeFolder)!;
        Assert.Equal(StepStatus.Failed, thrown.Status);
        Assert.Contains("Boom (simuliert)", thrown.LastError, StringComparison.Ordinal);
        Assert.Equal(StepStatus.Done, (await host.LoadAsync(ok)).FindStep(LogonScript)!.Status);

        await host.PollAsync(12);
        var hung = await host.LoadAsync(hang);
        Assert.True(AllStepsFinished(hung), Describe(hung));
        Assert.True(AllStepsFinished(await host.LoadAsync(ok)));
    }

    [Fact]
    public async Task No_secret_in_output_audit_or_errors()
    {
        using var host = new WorkerTestHost();
        var id = await host.CreateApprovedAsync();
        await host.PollAsync(12);

        var request = await host.LoadAsync(id);
        var texts = (await host.AuditAsync(id)).Select(a => a.Details ?? "")
            .Concat(request.Steps.Select(s => $"{s.OutputJson} {s.LastError} {s.Note}"));
        Assert.DoesNotContain(texts, t => t.Contains("Fake-Startpasswort", StringComparison.Ordinal));
    }
}

public sealed class FakeExecutorIdempotencyTests
{
    [Fact]
    public async Task Every_executor_run_twice_has_no_second_side_effect() // AK 2
    {
        var world = new FakeWorld(WorkerTestHost.DefaultWorld());
        var executors = FakeStepExecutors.Create(world).ToDictionary(e => e.StepKey);
        var factory = new Onboarding.Tests.TestSupport.RequestFactory();
        var request = factory.Approved(Onboarding.Tests.TestSupport.TestConfig.Person(extension: "12"));
        Guid? objectGuid = null;

        // Wait-type steps are polled until done; then each step is executed a second time.
        foreach (var key in new OnboardingStepPlan().Steps.Select(s => s.Key))
        {
            var outcome = await RunUntilFinishedAsync(executors[key], Context(request, objectGuid));
            Assert.Equal(StepOutcomeKind.Done, outcome.Kind);
            objectGuid ??= outcome.DirectoryObjectGuid;

            var writes = world.WriteOperations;
            var again = await executors[key].ExecuteAsync(Context(request, objectGuid), CancellationToken.None);
            Assert.True(again.Kind == StepOutcomeKind.Done, $"{key}: {again.Kind} {again.Message}");
            if (key is not (SyncDelta or SyncDeltaAfterEnable))
            {
                Assert.True(writes == world.WriteOperations, $"{key} wrote again");
            }
        }

        Assert.Single(world.AdUsers);
        Assert.Single(world.Files);
        Assert.Equal(9, world.FreeLicenses["SPB"]);
    }

    [Fact]
    public async Task Create_user_corrects_drifted_attributes()
    {
        var world = new FakeWorld(WorkerTestHost.DefaultWorld());
        var executor = new FakeAdCreateUser(world);
        var request = new Onboarding.Tests.TestSupport.RequestFactory().Approved();
        var first = await executor.ExecuteAsync(Context(request, null), CancellationToken.None);
        world.AdUsers["lirion"].Attributes["department"] = "Falsch";

        var second = await executor.ExecuteAsync(Context(request, first.DirectoryObjectGuid), CancellationToken.None);

        Assert.Equal(StepOutcomeKind.Done, second.Kind);
        Assert.Contains("department", second.Message, StringComparison.Ordinal);
        Assert.Equal("Vertrieb", world.AdUsers["lirion"].Attributes["department"]);
    }

    private static StepContext Context(Request request, Guid? objectGuid) =>
        new(request.Id, request.Input, request.Derived!, request.ConfigSnapshot!, false, objectGuid,
            () => new Onboarding.Core.Security.SecretString("x"));

    private static async Task<StepOutcome> RunUntilFinishedAsync(IStepExecutor executor, StepContext context)
    {
        for (var i = 0; i < 20; i++)
        {
            var outcome = await executor.ExecuteAsync(context, CancellationToken.None);
            if (outcome.Kind != StepOutcomeKind.Waiting)
            {
                return outcome;
            }
        }

        throw new InvalidOperationException($"{executor.StepKey} kept waiting.");
    }
}

public sealed class SqliteAndLockTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "onboarding-tests", Guid.NewGuid().ToString("N"));

    public SqliteAndLockTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public async Task Wal_mode_and_parallel_writers_do_not_lock()
    {
        using var host = new WorkerTestHost();
        await using (var db = host.DbFactory.CreateDbContext())
        {
            await db.Database.OpenConnectionAsync();
            var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", (string?)await command.ExecuteScalarAsync());
        }

        // Two independent "processes" (separate contexts/connections) write concurrently.
        var writers = Enumerable.Range(0, 2).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                await using var db = host.DbFactory.CreateDbContext();
                db.AuditLog.Add(AuditEntry.Create(host.Time.GetUtcNow(), $"writer{w}", "Test", AuditResults.Success, details: i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                await db.SaveChangesAsync();
            }
        }));
        await Task.WhenAll(writers);

        await using var check = host.DbFactory.CreateDbContext();
        Assert.Equal(100, await check.AuditLog.CountAsync(a => a.Action == "Test"));
    }

    [Fact]
    public void Second_worker_instance_is_refused()
    {
        var database = Path.Combine(_directory, "onboarding.db");
        var mutex = $@"Local\Onboarding.Worker.Test.{Guid.NewGuid():N}";
        using (SingleInstanceLock.Acquire(database, mutex))
        {
            Assert.Throws<InvalidOperationException>(() => SingleInstanceLock.Acquire(database, mutex));
        }

        using var again = SingleInstanceLock.Acquire(database, mutex);
        Assert.NotNull(again);
    }
}
