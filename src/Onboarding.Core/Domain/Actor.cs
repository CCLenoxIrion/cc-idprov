namespace Onboarding.Core.Domain;

/// <summary>The user (or the system) performing an action.</summary>
public sealed record Actor(string Name, IReadOnlySet<Role> Roles)
{
    /// <summary>Actor name used for automatic transitions by the worker.</summary>
    public const string SystemName = "system";

    public static Actor System { get; } = new(SystemName, new HashSet<Role>());

    public bool IsITAdmin => Roles.Contains(Role.ITAdmin);
    public bool IsRequester => Roles.Contains(Role.Requester);

    public static Actor Create(string name, params Role[] roles) => new(name, roles.ToHashSet());
}
