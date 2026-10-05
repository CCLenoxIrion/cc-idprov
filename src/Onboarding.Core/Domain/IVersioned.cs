namespace Onboarding.Core.Domain;

/// <summary>
/// Entity with an optimistic concurrency token. The persistence layer increments
/// <see cref="Version"/> on every update (SQLite has no generated rowversion).
/// </summary>
public interface IVersioned
{
    long Version { get; set; }
}
