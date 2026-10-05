namespace Onboarding.Data.Entities;

public enum ConfigChangeType
{
    Added,
    Modified,
    Deleted,
}

/// <summary>
/// Configuration change history (SPEC §4: who/when/old/new). Written by the save interceptor,
/// append-only.
/// </summary>
public sealed class ConfigHistoryEntry
{
    public long Id { get; set; }
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public ConfigChangeType ChangeType { get; set; }
    public string? OldJson { get; set; }
    public string? NewJson { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTimeOffset ChangedAt { get; set; }
}
