using Onboarding.Core.Domain;
using Onboarding.Core.State;

namespace Onboarding.Core.Steps;

/// <summary>Decides which steps of a request are due (SPEC §6, precisions P1/P2).</summary>
public static class StepScheduler
{
    /// <summary>
    /// Steps that may be claimed now, in plan order: Pending or Waiting, due
    /// (<see cref="RequestStep.NextAttemptAt"/> reached – before that a step is not touched at
    /// all), all dependencies Done/Skipped. Empty while another step of the request is Running
    /// (at most one Running step per request). A Waiting step does not block other due steps.
    /// </summary>
    public static IReadOnlyList<RequestStep> DueSteps(Request request, IStepPlanProvider plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        if (!RequestStateMachine.IsProcessable(request.Status) || request.ConfigSnapshot is null ||
            request.Steps.Any(s => s.Status == StepStatus.Running))
        {
            return [];
        }

        var byKey = request.Steps.ToDictionary(s => s.StepKey, StringComparer.Ordinal);
        var due = new List<RequestStep>();
        foreach (var definition in plan.Steps)
        {
            if (!byKey.TryGetValue(definition.Key, out var step) ||
                step.Status is not (StepStatus.Pending or StepStatus.Waiting) ||
                (step.NextAttemptAt is { } next && next > now))
            {
                continue;
            }

            if (definition.DependsOn.All(d => byKey.TryGetValue(d, out var dep) && StepStateMachine.SatisfiesDependency(dep.Status)))
            {
                due.Add(step);
            }
        }

        return due;
    }
}
