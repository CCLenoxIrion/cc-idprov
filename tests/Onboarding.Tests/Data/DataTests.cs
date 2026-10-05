using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Onboarding.Core.Domain;
using Onboarding.Core.Naming;
using Onboarding.Data;
using Onboarding.Data.Entities;
using Onboarding.Data.Seed;
using Onboarding.Tests.TestSupport;
using static Onboarding.Core.Steps.StepKeys;

namespace Onboarding.Tests.Data;

public sealed class DataTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
    private readonly RequestFactory _f = new();

    public DataTests()
    {
        _connection.Open();
        using var db = Context();
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private OnboardingDbContext Context(string actor = "tester")
    {
        var builder = new DbContextOptionsBuilder<OnboardingDbContext>();
        builder.UseOnboardingSqlite(_connection, new FixedActorAccessor(actor), _time);
        return new OnboardingDbContext(builder.Options);
    }

    private async Task<Guid> SaveApprovedRequest(PersonInput? input = null)
    {
        var request = _f.Approved(input);
        await using var db = Context();
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    [Fact]
    public async Task Migration_applies_seed_data()
    {
        await using var db = Context();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var global = await db.GlobalConfig.SingleAsync();
        Assert.Equal(SeedData.Global().MailPattern, global.Settings.MailPattern);
        Assert.Equal(["SMTP:{mailLocal}@cleancontrolling.de", "smtp:{mailLocal}@cleancontrolling.com"], global.Settings.ProxyAddressTemplates);
        Assert.Equal("Europe/Berlin", global.Settings.TimeZone);
        Assert.Equal(3, await db.Areas.CountAsync());
        Assert.Equal(7, await db.ChecklistTemplates.CountAsync(t => t.Type == RequestType.Onboarding && t.Mandatory));
    }

    [Fact]
    public async Task Request_roundtrip_with_steps_checklist_and_json_columns()
    {
        var id = await SaveApprovedRequest(TestConfig.Person(extension: "12", doctor: true));

        await using var db = Context();
        var loaded = await db.Requests.SingleAsync(r => r.Id == id);

        Assert.Equal(RequestStatus.Approved, loaded.Status);
        Assert.Equal("Lenox", loaded.Input.FirstName);
        Assert.Equal(new DateOnly(2026, 11, 2), loaded.Input.EffectiveDate);
        Assert.Equal("lirion", loaded.Derived!.SamAccountName);
        Assert.Equal("+49746592967812", loaded.Derived.PhoneE164);
        Assert.Equal("Dr.", loaded.Derived.AdditionalAttributes["extensionAttribute1"]);
        Assert.Equal("Vertrieb", loaded.ConfigSnapshot!.Department.Name);
        Assert.Equal(19, loaded.Steps.Count);
        Assert.Equal(3, loaded.Checklist.Count);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero),
            loaded.FindStep(AdEnable)!.NextAttemptAt);
    }

    [Fact]
    public async Task Due_steps_can_be_queried_by_time()
    {
        await SaveApprovedRequest();

        await using var db = Context();
        var now = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        var due = await db.RequestSteps
            .Where(s => s.Status == StepStatus.Pending && (s.NextAttemptAt == null || s.NextAttemptAt <= now))
            .Select(s => s.StepKey)
            .ToListAsync();

        Assert.Contains(AdCreateUser, due);
        Assert.DoesNotContain(AdEnable, due); // entry date in the future
    }

    [Fact]
    public async Task Audit_log_is_append_only()
    {
        await using (var db = Context())
        {
            db.AuditLog.Add(AuditEntry.Create(_time.GetUtcNow(), "tester", "Test", AuditResults.Success));
            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var entry = await db.AuditLog.SingleAsync();
            db.AuditLog.Remove(entry);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = Context())
        {
            var entry = await db.AuditLog.SingleAsync();
            db.Entry(entry).Property(e => e.Details).CurrentValue = "manipuliert";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = Context())
        {
            Assert.Null((await db.AuditLog.SingleAsync()).Details);
        }
    }

    [Fact]
    public async Task Config_changes_are_recorded_with_actor_old_and_new()
    {
        await using (var db = Context("it.admin"))
        {
            var global = await db.GlobalConfig.SingleAsync();
            global.Settings.PhoneDisplayFormat = "+49 7465 929678 {DW}"; // in-place mutation of JSON column
            var area = await db.Areas.SingleAsync(a => a.Name == "TecSa");
            area.Company = "CleanControlling GmbH (neu)";
            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var history = await db.ConfigHistory.OrderBy(h => h.EntityType).ToListAsync();

            Assert.Equal(2, history.Count);
            Assert.All(history, h =>
            {
                Assert.Equal("it.admin", h.ChangedBy);
                Assert.Equal(ConfigChangeType.Modified, h.ChangeType);
                Assert.Equal(_time.GetUtcNow(), h.ChangedAt);
            });

            var areaHistory = history.Single(h => h.EntityType == nameof(Onboarding.Core.Configuration.AreaConfig));
            Assert.Contains("\"Company\":\"CleanControlling GmbH\"", areaHistory.OldJson, StringComparison.Ordinal);
            Assert.Contains("CleanControlling GmbH (neu)", areaHistory.NewJson, StringComparison.Ordinal);

            var globalHistory = history.Single(h => h.EntityType == nameof(GlobalConfigRecord));
            Assert.Contains("929678-{DW}", globalHistory.OldJson, StringComparison.Ordinal);
            Assert.Contains("929678 {DW}", globalHistory.NewJson, StringComparison.Ordinal);

            Assert.Equal(1, (await db.GlobalConfig.SingleAsync()).Version);
        }
    }

    [Fact]
    public async Task Config_add_and_delete_are_recorded()
    {
        var id = Guid.NewGuid();
        await using (var db = Context("it.admin"))
        {
            db.Departments.Add(new Onboarding.Core.Configuration.DepartmentConfig { Id = id, Name = "Labor" });
            await db.SaveChangesAsync();
        }

        await using (var db = Context("it.admin2"))
        {
            db.Departments.Remove(await db.Departments.SingleAsync(d => d.Id == id));
            await db.SaveChangesAsync();
        }

        await using (var check = Context())
        {
            var history = await check.ConfigHistory.Where(h => h.EntityId == id.ToString()).OrderBy(h => h.Id).ToListAsync();
            Assert.Equal([ConfigChangeType.Added, ConfigChangeType.Deleted], history.Select(h => h.ChangeType));
            Assert.Null(history[0].OldJson);
            Assert.Null(history[1].NewJson);
            Assert.Equal("it.admin2", history[1].ChangedBy);
        }
    }

    [Fact]
    public async Task Unchanged_config_save_writes_no_history()
    {
        await using (var db = Context())
        {
            _ = await db.GlobalConfig.SingleAsync();
            await db.SaveChangesAsync();
        }

        await using var check = Context();
        Assert.Empty(await check.ConfigHistory.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_step_update_throws_concurrency_exception()
    {
        var id = await SaveApprovedRequest();

        await using var first = Context();
        await using var second = Context();
        var stepA = await first.RequestSteps.SingleAsync(s => s.RequestId == id && s.StepKey == AdCreateUser);
        var stepB = await second.RequestSteps.SingleAsync(s => s.RequestId == id && s.StepKey == AdCreateUser);

        stepA.TransitionTo(StepStatus.Running, _time.GetUtcNow());
        await first.SaveChangesAsync();
        Assert.Equal(1, stepA.Version);

        stepB.TransitionTo(StepStatus.Running, _time.GetUtcNow());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_request_update_throws_concurrency_exception()
    {
        var id = await SaveApprovedRequest();

        await using var first = Context();
        await using var second = Context();
        var a = await first.Requests.SingleAsync(r => r.Id == id);
        var b = await second.Requests.SingleAsync(r => r.Id == id);

        _f.Workflow.Cancel(a, RequestFactory.Admin, "erster");
        await first.SaveChangesAsync();

        _f.Workflow.Cancel(b, RequestFactory.Admin2, "zweiter");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Open_request_lookup_returns_reserved_identities_of_other_open_requests()
    {
        var mine = await SaveApprovedRequest();
        var other = await SaveApprovedRequest(TestConfig.Person("Lisa", "Irion"));
        var cancelled = _f.Approved(TestConfig.Person("Max", "Mustermann"));
        _f.Workflow.Cancel(cancelled, RequestFactory.Admin, "test");
        await using (var db = Context())
        {
            db.Requests.Add(cancelled);
            await db.SaveChangesAsync();
        }

        await using var context = Context();
        var lookup = new OpenRequestLookup(context);
        var open = await lookup.GetOpenRequestIdentitiesAsync(mine, CancellationToken.None);

        var single = Assert.Single(open);
        Assert.Equal(other, single.RequestId);
        Assert.Equal("lirion", single.SamAccountName);

        // "lirion" for Lisa Irion collides with the open request of Lenox Irion.
        var collisions = await new CollisionChecker(new FakeDirectory(), lookup)
            .CheckAsync(mine, (await context.Requests.SingleAsync(r => r.Id == mine)).Derived!);
        Assert.Contains(collisions, c => c.Field == CollisionField.SamAccountName && c.Source == CollisionSource.OpenRequest);
    }

    [Fact]
    public async Task Audit_entries_from_workflow_persist_without_secrets()
    {
        var request = _f.Draft();
        var audits = new List<AuditEntry>();
        audits.AddRange(_f.Workflow.Submit(request, RequestFactory.Hr, RequestFactory.Check(request.Input)));
        audits.AddRange(_f.Workflow.Approve(request, RequestFactory.Admin, TestConfig.Snapshot(), RequestFactory.Check(request.Input), [9, 9, 9]));

        await using (var db = Context())
        {
            db.Requests.Add(request);
            db.AuditLog.AddRange(audits);
            await db.SaveChangesAsync();
        }

        await using var check = Context();
        var stored = await check.AuditLog.Where(a => a.RequestId == request.Id).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, a => a.Action == AuditActions.RequestApproved && a.Actor == "it.admin");
    }
}
