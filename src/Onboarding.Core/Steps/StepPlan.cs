using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;

namespace Onboarding.Core.Steps;

/// <summary>Input for deciding which steps apply to a request.</summary>
public sealed record StepPlanContext(PersonInput Input, RequestConfigSnapshot Snapshot)
{
    public bool HasExtension => !string.IsNullOrWhiteSpace(Input.Extension);

    /// <summary>Teams phone steps run only with an extension and if the department assigns phones.</summary>
    public bool AssignsPhone => HasExtension && Snapshot.Department.Teams.AssignPhone;
}

/// <summary>
/// Static description of a step: dependencies, applicability and earliest start.
/// </summary>
/// <param name="Key">Step key, see <see cref="StepKeys"/>.</param>
/// <param name="DependsOn">Steps that must be Done or Skipped first.</param>
/// <param name="SkipReason">Returns a reason if the step does not apply (→ Skipped), else null.</param>
/// <param name="NotBefore">Earliest start, e.g. the entry date for <c>AD.Enable</c>; null = no constraint.</param>
public sealed record StepDefinition(
    string Key,
    IReadOnlyList<string> DependsOn,
    Func<StepPlanContext, string?>? SkipReason = null,
    Func<StepPlanContext, DateTimeOffset?>? NotBefore = null);

/// <summary>Step plan for one request type.</summary>
public interface IStepPlanProvider
{
    RequestType Type { get; }

    /// <summary>Steps in display/topological order.</summary>
    IReadOnlyList<StepDefinition> Steps { get; }
}

/// <summary>Resolves the step plan for a request type.</summary>
public sealed class StepPlanRegistry
{
    private readonly Dictionary<RequestType, IStepPlanProvider> _providers;

    public StepPlanRegistry(IEnumerable<IStepPlanProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(p => p.Type);
    }

    public static StepPlanRegistry Default { get; } = new([new OnboardingStepPlan()]);

    public bool Supports(RequestType type) => _providers.ContainsKey(type);

    public IStepPlanProvider For(RequestType type) =>
        _providers.TryGetValue(type, out var provider)
            ? provider
            : throw new NotSupportedException($"No step plan registered for request type {type}.");
}
