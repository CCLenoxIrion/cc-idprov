using Onboarding.Core.Steps;

namespace Onboarding.Worker;

/// <summary>Worker log messages (per-attempt details go here, not into the audit log; P3).</summary>
internal static partial class WorkerLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovering interrupted step {StepKey} of request {RequestId}")]
    public static partial void RecoveringStep(this ILogger logger, string stepKey, Guid requestId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing request {RequestId} failed")]
    public static partial void RequestFailed(this ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Step {StepKey} of request {RequestId} was claimed concurrently")]
    public static partial void ClaimConflict(this ILogger logger, string stepKey, Guid requestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running {StepKey} for request {RequestId} (attempt {Attempt})")]
    public static partial void RunningStep(this ILogger logger, string stepKey, Guid requestId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Step {StepKey} exceeded the execution timeout of {Timeout}")]
    public static partial void ExecutionTimeout(this ILogger logger, string stepKey, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Error, Message = "Step {StepKey} threw {ExceptionType}")]
    public static partial void StepThrew(this ILogger logger, string stepKey, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Step {StepKey} of request {RequestId} is no longer running; outcome dropped")]
    public static partial void OutcomeDropped(this ILogger logger, string stepKey, Guid requestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{StepKey} for request {RequestId}: {Outcome} {Message}")]
    public static partial void StepOutcomeApplied(this ILogger logger, string stepKey, Guid requestId, StepOutcomeKind outcome, string? message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Worker pass failed")]
    public static partial void PassFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Certificate check failed")]
    public static partial void CertificateCheckFailed(this ILogger logger, Exception exception);
}
