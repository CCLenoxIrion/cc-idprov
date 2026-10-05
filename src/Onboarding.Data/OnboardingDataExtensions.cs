using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Onboarding.Data.Interceptors;

namespace Onboarding.Data;

public static class OnboardingDataExtensions
{
    /// <summary>Configures SQLite plus the mandatory save interceptor.</summary>
    public static DbContextOptionsBuilder UseOnboardingSqlite(
        this DbContextOptionsBuilder builder,
        string connectionString,
        IActorAccessor actor,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .UseSqlite(connectionString)
            .AddInterceptors(new OnboardingSaveChangesInterceptor(actor, timeProvider));
    }

    /// <summary>Configures an already opened SQLite connection (tests, in-memory DBs).</summary>
    public static DbContextOptionsBuilder UseOnboardingSqlite(
        this DbContextOptionsBuilder builder,
        System.Data.Common.DbConnection connection,
        IActorAccessor actor,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .UseSqlite(connection)
            .AddInterceptors(new OnboardingSaveChangesInterceptor(actor, timeProvider));
    }
}

/// <summary>Used by <c>dotnet ef</c> at design time only.</summary>
public sealed class OnboardingDbContextFactory : IDesignTimeDbContextFactory<OnboardingDbContext>
{
    public OnboardingDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<OnboardingDbContext>();
        builder.UseOnboardingSqlite("Data Source=onboarding.db", new FixedActorAccessor("design-time"), TimeProvider.System);
        return new OnboardingDbContext(builder.Options);
    }
}
