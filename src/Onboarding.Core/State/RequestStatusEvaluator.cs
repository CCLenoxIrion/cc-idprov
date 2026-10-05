using Onboarding.Core.Domain;

namespace Onboarding.Core.State;

/// <summary>
/// Derives the request status from its steps and checklist (SPEC §6.2, AK 7). Pure function;
/// the worker applies the result through <see cref="Workflow.RequestWorkflow.ApplyEvaluation"/>.
/// </summary>
public static class RequestStatusEvaluator
{
    /// <summary>
    /// Returns the status the request should have, or the current status if it is not in an
    /// executing state (Draft, PendingApproval, NeedsInput, terminal states are left alone).
    /// </summary>
    public static RequestStatus Evaluate(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!RequestStateMachine.IsExecuting(request.Status) && request.Status != RequestStatus.Failed)
        {
            return request.Status;
        }

        var steps = request.Steps;
        if (steps.Count == 0)
        {
            return request.Status;
        }

        if (steps.Any(s => s.Status == StepStatus.Failed))
        {
            return RequestStatus.Failed;
        }

        if (steps.All(s => StepStateMachine.SatisfiesDependency(s.Status)))
        {
            return request.Checklist.Where(c => c.Mandatory).All(c => c.IsSatisfied)
                ? RequestStatus.Completed
                : RequestStatus.AwaitingChecklist;
        }

        if (steps.Any(s => s.Status == StepStatus.Running))
        {
            return RequestStatus.Running;
        }

        // Nothing running: waiting for a retry, a date (AD.Enable), or an admin (ManualTask).
        if (steps.Any(s => s.Status is StepStatus.Waiting or StepStatus.ManualTask))
        {
            return RequestStatus.Waiting;
        }

        // Only Pending steps left that are not yet due (e.g. AD.Enable before the entry date).
        return request.Status == RequestStatus.Approved && steps.All(s => s.Status != StepStatus.Done)
            ? RequestStatus.Approved
            : RequestStatus.Running;
    }
}
