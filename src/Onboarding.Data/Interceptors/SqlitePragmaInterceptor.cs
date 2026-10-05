using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Onboarding.Data.Interceptors;

/// <summary>
/// SQLite settings for concurrent access by web and worker on the same host (DECISIONS B1/B2):
/// WAL journal and a busy timeout. This is the only SQLite-specific code besides the provider
/// registration; the DbContext and all queries stay provider-neutral.
/// </summary>
public sealed class SqlitePragmaInterceptor(TimeSpan busyTimeout) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = CreateCommand(connection);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed text; the only value is a validated integer.
        command.CommandText = string.Create(CultureInfo.InvariantCulture,
            $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={(int)busyTimeout.TotalMilliseconds};");
#pragma warning restore CA2100
        return command;
    }
}
