namespace Onboarding.Data;

/// <summary>Who is saving changes; used for the configuration history.</summary>
public interface IActorAccessor
{
    string CurrentActor { get; }
}

public sealed class FixedActorAccessor(string actor) : IActorAccessor
{
    public string CurrentActor { get; } = actor;
}
