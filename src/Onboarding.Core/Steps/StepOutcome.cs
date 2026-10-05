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
/// <param name="Code">Fixed reason code (DECISIONS X10), e.g. <c>ou-mismatch</c>; UI and tests check it, not the text.</param>
public sealed record StepOutcome(
    StepOutcomeKind Kind,
    string? Message = null,
    string? OutputJson = null,
    Guid? DirectoryObjectGuid = null,
    string? Code = null)
{
    public static StepOutcome Done(string? message = null, string? outputJson = null, Guid? directoryObjectGuid = null) =>
        new(StepOutcomeKind.Done, message, outputJson, directoryObjectGuid);

    public static StepOutcome Waiting(string reason, string? code = null) => new(StepOutcomeKind.Waiting, reason, Code: code);

    public static StepOutcome Failed(string error, string? code = null) => new(StepOutcomeKind.Failed, error, Code: code);

    public static StepOutcome NeedsInput(string reason, string? code = null) => new(StepOutcomeKind.NeedsInput, reason, Code: code);

    public static StepOutcome ManualTask(string instructions, string? code = null) => new(StepOutcomeKind.ManualTask, instructions, Code: code);

    public static StepOutcome Skipped(string reason, string? code = null) => new(StepOutcomeKind.Skipped, reason, Code: code);
}

/// <summary>Reason codes (DECISIONS X10): kebab-case, stored with the step.</summary>
public static class ReasonCodes
{
    public const int MaxLength = 49;

    /// <summary>Step timeout exceeded while Waiting (SPEC §6).</summary>
    public const string StepTimeout = "step-timeout";

    public static bool IsValid(string? code) =>
        code is { Length: >= 2 and <= MaxLength } &&
        char.IsAsciiLetterLower(code[0]) &&
        code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
