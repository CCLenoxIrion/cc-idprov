using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Data.Conversion;
using Onboarding.Data.Entities;

namespace Onboarding.Data.Interceptors;

/// <summary>
/// Enforces persistence rules on every save:
/// <list type="bullet">
/// <item>audit log and config history are append-only,</item>
/// <item>concurrency tokens (<see cref="IVersioned.Version"/>) are incremented on update,</item>
/// <item>configuration changes are recorded with who/when/old/new.</item>
/// </list>
/// </summary>
public sealed class OnboardingSaveChangesInterceptor(IActorAccessor actor, TimeProvider timeProvider) : SaveChangesInterceptor
{
    private static readonly HashSet<Type> ConfigTypes =
    [
        typeof(GlobalConfigRecord),
        typeof(AreaConfig),
        typeof(DepartmentConfig),
        typeof(ChecklistTemplate),
    ];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var entries = context.ChangeTracker.Entries().ToList();

        foreach (var entry in entries)
        {
            if (entry.Entity is AuditEntry or ConfigHistoryEntry &&
                entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException($"{entry.Entity.GetType().Name} is append-only.");
            }
        }

        var now = timeProvider.GetUtcNow();
        var history = new List<ConfigHistoryEntry>();
        foreach (var entry in entries)
        {
            if (!ConfigTypes.Contains(entry.Entity.GetType()) ||
                entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var oldJson = entry.State == EntityState.Added ? null : Snapshot(entry, original: true);
            var newJson = entry.State == EntityState.Deleted ? null : Snapshot(entry, original: false);
            if (entry.State == EntityState.Modified && oldJson == newJson)
            {
                continue;
            }

            history.Add(new ConfigHistoryEntry
            {
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = string.Join(",", entry.Metadata.FindPrimaryKey()!.Properties
                    .Select(p => Convert.ToString(entry.Property(p.Name).CurrentValue, System.Globalization.CultureInfo.InvariantCulture))),
                ChangeType = entry.State switch
                {
                    EntityState.Added => ConfigChangeType.Added,
                    EntityState.Deleted => ConfigChangeType.Deleted,
                    _ => ConfigChangeType.Modified,
                },
                OldJson = oldJson,
                NewJson = newJson,
                ChangedBy = actor.CurrentActor,
                ChangedAt = now,
            });
        }

        // After the history snapshot, so the version bump itself is not recorded as a change.
        foreach (var entry in entries)
        {
            if (entry.Entity is IVersioned versioned && entry.State == EntityState.Modified)
            {
                versioned.Version++;
            }
        }

        context.AddRange(history);
    }

    private static string Snapshot(EntityEntry entry, bool original)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entry.Properties)
        {
            if (property.Metadata.Name == nameof(IVersioned.Version))
            {
                continue;
            }

            values[property.Metadata.Name] = original ? property.OriginalValue : property.CurrentValue;
        }

        return JsonColumn.Serialize(values);
    }
}
