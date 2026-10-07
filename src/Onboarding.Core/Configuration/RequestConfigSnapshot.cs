namespace Onboarding.Core.Configuration;

/// <summary>
/// Configuration frozen at approval time (DECISIONS S3). Steps read only from this snapshot,
/// so later config changes do not affect running requests unless an ITAdmin refreshes it.
/// Contains no secrets.
/// </summary>
public sealed class RequestConfigSnapshot
{
    public GlobalConfig Global { get; set; } = new();
    public AreaConfig Area { get; set; } = new();
    public DepartmentConfig Department { get; set; } = new();
    public DateTimeOffset TakenAt { get; set; }
}
