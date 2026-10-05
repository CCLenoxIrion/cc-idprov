using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Onboarding.Core.Configuration;
using Onboarding.Data;

namespace Onboarding.Tests.Data;

/// <summary>Migration Phase4bHomeAclLogonServer: adds missing keys, never overwrites administered values.</summary>
public sealed class HomeAclMigrationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));

    public HomeAclMigrationTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private OnboardingDbContext Context()
    {
        var builder = new DbContextOptionsBuilder<OnboardingDbContext>();
        builder.UseOnboardingSqlite(_connection, new FixedActorAccessor("tester"), _time);
        return new OnboardingDbContext(builder.Options);
    }

    private static string Previous(OnboardingDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_Phase4bHomeAclLogonServer", StringComparison.Ordinal));
        return all[index - 1];
    }

    [Fact]
    public async Task Fresh_database_gets_the_seed_values()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();

        var settings = (await db.GlobalConfig.SingleAsync()).Settings;
        Assert.Equal("DC03", settings.LogonScript.Server);
        Assert.Equal(HomeRight.Modify, settings.Home.UserRight);
        Assert.Equal(["SYSTEM", @"BUILTIN\Administrators"], settings.Home.AdditionalAces.Select(a => a.Principal));
        Assert.All(settings.Home.AdditionalAces, a => Assert.Equal(HomeRight.FullControl, a.Right));
        Assert.Empty(ConfigValidator.ValidateGlobal(settings));
    }

    [Fact]
    public async Task Administered_values_are_kept_and_only_missing_keys_are_added()
    {
        await using (var db = Context())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous(db));
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE GlobalConfig SET Settings = json_set(Settings, '$.LogonScript.Server', 'DCX', '$.Home.Server', 'FS01')");
            await db.Database.ExecuteSqlRawAsync("UPDATE Departments SET Name = Name");
            await db.Database.MigrateAsync();
        }

        await using (var db = Context())
        {
            var settings = (await db.GlobalConfig.SingleAsync()).Settings;
            Assert.Equal("DCX", settings.LogonScript.Server);
            Assert.Equal("FS01", settings.Home.Server);
            Assert.Equal(HomeRight.Modify, settings.Home.UserRight);
            Assert.Equal(2, settings.Home.AdditionalAces.Count);
            Assert.All(await db.Departments.ToListAsync(), d => Assert.Empty(d.HomeAdditionalAces));
        }
    }
}
