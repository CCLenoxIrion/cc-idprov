namespace Onboarding.Core.Steps;

public enum StepOutcomeKind
{
    Done,
    Waiting,
    Failed,
    NeedsInput,
    ManualTask,
    Skipped,
}

/// <summary>
/// Result of one step execution, returned by an executor. <see cref="Message"/> and
/// <see cref="OutputJson"/> must never contain secrets (SPEC §9).
/// </summary>
/// <param name="Kind">Outcome.</param>
/// <param name="Message">Reason (Waiting/Failed/NeedsInput/Skipped), instructions (ManualTask) or summary (Done).</param>
/// <param name="OutputJson">Structured output for Done, e.g. the SHA-256 of a written file.</param>
/// <param name="DirectoryObjectGuid">objectGUID of the account created by <c>AD.CreateUser</c>.</param>
public sealed record StepOutcome(
    StepOutcomeKind Kind,
    string? Message = null,
    string? OutputJson = null,
    Guid? DirectoryObjectGuid = null)
{
    public static StepOutcome Done(string? message = null, string? outputJson = null, Guid? directoryObjectGuid = null) =>
        new(StepOutcomeKind.Done, message, outputJson, directoryObjectGuid);

    public static StepOutcome Waiting(string reason) => new(StepOutcomeKind.Waiting, reason);

    public static StepOutcome Failed(string error) => new(StepOutcomeKind.Failed, error);

    public static StepOutcome NeedsInput(string reason) => new(StepOutcomeKind.NeedsInput, reason);

    public static StepOutcome ManualTask(string instructions) => new(StepOutcomeKind.ManualTask, instructions);

    public static StepOutcome Skipped(string reason) => new(StepOutcomeKind.Skipped, reason);
}
