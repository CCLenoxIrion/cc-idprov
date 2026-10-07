using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Onboarding.Data.Interceptors;

namespace Onboarding.Data;

public static class OnboardingDataExtensions
{
    /// <summary>Default SQLite busy timeout (DECISIONS B2).</summary>
    public static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Configures SQLite (WAL, busy timeout) plus the mandatory save interceptor. The database
    /// file must be local to the host running web and worker, never on a network share (B1).
    /// </summary>
    public static DbContextOptionsBuilder UseOnboardingSqlite(
        this DbContextOptionsBuilder builder,
        string connectionString,
        IActorAccessor actor,
        TimeProvider timeProvider,
        TimeSpan? busyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .UseSqlite(connectionString, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery))
            .AddInterceptors(
                new SqlitePragmaInterceptor(busyTimeout ?? DefaultBusyTimeout),
                new OnboardingSaveChangesInterceptor(actor, timeProvider));
    }

    /// <summary>
    /// Resolves a relative <c>Data Source</c> against the host's content root, so web and worker
    /// use the same file regardless of the working directory.
    /// </summary>
    public static string ResolveConnectionString(string connectionString, string contentRoot)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        if (builder.DataSource != ":memory:" && !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(contentRoot, builder.DataSource));
        }

        return builder.ToString();
    }

    /// <summary>Full path of the SQLite database file from a connection string (for the worker lock).</summary>
    public static string DatabaseFilePath(string connectionString)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        return Path.GetFullPath(builder.DataSource);
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
            .UseSqlite(connection, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery))
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
