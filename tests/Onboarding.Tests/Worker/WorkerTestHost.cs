using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Core.Security;
using Onboarding.Core.Steps;
using Onboarding.Core.Workflow;
using Onboarding.Data;
using Onboarding.Steps.Execution;
using Onboarding.Steps.Fakes.World;
using Onboarding.Tests.TestSupport;
using Onboarding.Worker;

namespace Onboarding.Tests.Worker;

/// <summary>
/// Worker engine against a temporary SQLite file (WAL, separate connections per context) and the
/// simulated fake world. Time is a <see cref="FakeTimeProvider"/>.
/// </summary>
internal sealed class WorkerTestHost : IDisposable
{
    public static readonly Actor Hr = Actor.Create("hr.user", Role.Requester);
    public static readonly Actor Admin = Actor.Create("it.admin", Role.ITAdmin);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "onboarding-tests", Guid.NewGuid().ToString("N"));

    public WorkerTestHost(FakeWorldOptions? world = null, WorkerOptions? options = null)
    {
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "onboarding.db");
        ConnectionString = $"Data Source={DatabasePath}";
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        DbFactory = new FileDbFactory(ConnectionString, Time);
        using (var db = DbFactory.CreateDbContext())
        {
            db.Database.Migrate();
        }

        World = new FakeWorld(world ?? DefaultWorld());
        Executions = new ExecutionRecorder();
        Registry = new StepExecutorRegistry(FakeStepExecutors.Create(World).Select(e => new RecordingExecutor(e, Executions)));
        Options = options ?? new WorkerOptions { MaxParallelRequests = 3, ExecutionTimeout = TimeSpan.FromSeconds(5) };
        Engine = CreateEngine();
        Workflow = new RequestWorkflow(Time);
    }

    public string DatabasePath { get; }
    public string ConnectionString { get; }
    public FakeTimeProvider Time { get; }
    public FileDbFactory DbFactory { get; }
    public FakeWorld World { get; }
    public ExecutionRecorder Executions { get; }
    public StepExecutorRegistry Registry { get; }
    public WorkerOptions Options { get; }
    public WorkerEngine Engine { get; }
    public RequestWorkflow Workflow { get; }

    public static FakeWorldOptions DefaultWorld() => new()
    {
        FreeLicenses = new(StringComparer.OrdinalIgnoreCase) { ["SPB"] = 10, ["MCOEV"] = 10 },
    };

    public WorkerEngine CreateEngine() =>
        new(DbFactory, Registry, new FakeDecryptor(), Time, Options, NullLogger<WorkerEngine>.Instance);

    /// <summary>Creates, submits and approves a request; the entry date is relative to "today".</summary>
    public async Task<Guid> CreateApprovedAsync(
        string first = "Lenox",
        string last = "Irion",
        string? extension = "12",
        int entryInDays = 0,
        Action<RequestConfigSnapshot>? configure = null)
    {
        var input = TestConfig.Person(first, last, extension: extension);
        input.EffectiveDate = DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime).AddDays(entryInDays);
        var (request, created) = Workflow.Create(RequestType.Onboarding, input, Hr, RequestFactory.Templates());
        var audits = new List<AuditEntry> { created };
        audits.AddRange(Workflow.Submit(request, Hr, RequestFactory.Check(input)));
        var snapshot = TestConfig.Snapshot();
        configure?.Invoke(snapshot);
        audits.AddRange(Workflow.Approve(request, Admin, snapshot, RequestFactory.Check(input), [1, 2, 3]));
        Assert.Equal(RequestStatus.Approved, request.Status);

        await using var db = DbFactory.CreateDbContext();
        db.Requests.Add(request);
        db.AuditLog.AddRange(audits);
        await db.SaveChangesAsync();
        return request.Id;
    }

    public async Task<Request> LoadAsync(Guid id)
    {
        await using var db = DbFactory.CreateDbContext();
        return await db.Requests.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    public async Task<List<AuditEntry>> AuditAsync(Guid id)
    {
        await using var db = DbFactory.CreateDbContext();
        return await db.AuditLog.AsNoTracking().Where(a => a.RequestId == id).OrderBy(a => a.Id).ToListAsync();
    }

    /// <summary>Applies a workflow action to a stored request and saves it with its audit entries.</summary>
    public async Task MutateAsync(Guid id, Func<Request, IReadOnlyList<AuditEntry>> action)
    {
        await using var db = DbFactory.CreateDbContext();
        var request = await db.Requests.SingleAsync(r => r.Id == id);
        db.AuditLog.AddRange(action(request));
        await db.SaveChangesAsync();
    }

    /// <summary>Runs polls, advancing time by <paramref name="step"/> after each.</summary>
    public async Task PollAsync(int polls, TimeSpan? step = null)
    {
        for (var i = 0; i < polls; i++)
        {
            await Engine.RunOnceAsync(CancellationToken.None);
            Time.Advance(step ?? TimeSpan.FromMinutes(15));
        }
    }

    public async Task TickAllMandatoryAsync(Guid id)
    {
        var request = await LoadAsync(id);
        foreach (var item in request.Checklist.Where(c => c.Mandatory))
        {
            await MutateAsync(id, r => Workflow.SetChecklistItemStatus(r, item.Id, ChecklistItemStatus.Done, null, Admin));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    internal sealed class FileDbFactory(string connectionString, TimeProvider time) : IDbContextFactory<OnboardingDbContext>
    {
        public OnboardingDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<OnboardingDbContext>();
            builder.UseOnboardingSqlite(connectionString, new FixedActorAccessor("worker"), time);
            return new OnboardingDbContext(builder.Options);
        }
    }

    /// <summary>Stands in for the certificate decryption; never returns the real value of anything.</summary>
    internal sealed class FakeDecryptor : ISecretDecryptor
    {
        public SecretString Decrypt(byte[] ciphertext) => new("Fake-Startpasswort-1!");
    }

    /// <summary>Records executions and the number of concurrently running steps per request.</summary>
    internal sealed class ExecutionRecorder
    {
        private readonly ConcurrentDictionary<Guid, int> _running = new();

        public ConcurrentQueue<(Guid RequestId, string StepKey)> Calls { get; } = new();

        public int MaxConcurrentPerRequest { get; private set; }

        public void Enter(Guid requestId, string stepKey)
        {
            Calls.Enqueue((requestId, stepKey));
            var now = _running.AddOrUpdate(requestId, 1, (_, v) => v + 1);
            lock (this)
            {
                MaxConcurrentPerRequest = Math.Max(MaxConcurrentPerRequest, now);
            }
        }

        public void Exit(Guid requestId) => _running.AddOrUpdate(requestId, 0, (_, v) => v - 1);

        public int Count(Guid requestId, string stepKey) => Calls.Count(c => c.RequestId == requestId && c.StepKey == stepKey);
    }

    private sealed class RecordingExecutor(IStepExecutor inner, ExecutionRecorder recorder) : IStepExecutor
    {
        public string StepKey => inner.StepKey;

        public async Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
        {
            recorder.Enter(context.RequestId, StepKey);
            try
            {
                await Task.Delay(2, cancellationToken);
                return await inner.ExecuteAsync(context, cancellationToken);
            }
            finally
            {
                recorder.Exit(context.RequestId);
            }
        }
    }
}
