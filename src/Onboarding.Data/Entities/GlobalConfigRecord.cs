using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;

namespace Onboarding.Data.Entities;

/// <summary>Single row holding the global configuration (SPEC §4.1) as JSON.</summary>
public sealed class GlobalConfigRecord : IVersioned
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public GlobalConfig Settings { get; set; } = new();
    public long Version { get; set; }
}
