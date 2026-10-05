using System.Text.Json;
using Microsoft.Extensions.Logging;
using Onboarding.Core.Steps;
using Onboarding.Steps.Execution;

namespace Onboarding.Steps.Scripts;

/// <summary>
/// Runs <c>scripts/steps/&lt;StepKey&gt;.ps1</c> with pwsh (DECISIONS X3). Input only via stdin
/// JSON, output only JSON on stdout. stderr never reaches step errors or the audit log (X7).
/// In dry-run mode planned actions become NeedsInput with a report (X4).
/// </summary>
public sealed partial class ScriptStepExecutor(
    string stepKey,
    IntegrationMode mode,
    ScriptOptions options,
    IProcessRunner runner,
    ILogger<ScriptStepExecutor> logger) : IStepExecutor
{
    private const int MaxReasonLength = 1000;
    private const int MaxLoggedStderr = 2000;

    public string StepKey { get; } = stepKey;

    public IntegrationMode Mode { get; } = mode is IntegrationMode.Fake
        ? throw new ArgumentOutOfRangeException(nameof(mode), "Script executors run in DryRun or Real mode only.")
        : mode;

    public async Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = StepScriptJson.BuildInput(context, StepKey, Mode == IntegrationMode.DryRun, options);
        var json = JsonSerializer.Serialize(input, StepScriptJson.Options);
        var script = Path.Combine(options.ScriptsDirectory, "steps", StepKey + ".ps1");
        string[] arguments = ["-NoProfile", "-NonInteractive", "-File", script];

        var result = await runner.RunAsync(options.PwshPath, arguments, json, options.Timeout, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            LogStderr(logger, StepKey, Redact(Truncate(result.StandardError, MaxLoggedStderr), input.InitialPassword));
        }

        if (result.TimedOut)
        {
            return StepOutcome.Waiting($"Skript nach {options.Timeout} abgebrochen; neuer Versuch folgt.", "script-timeout");
        }

        if (result.ExitCode != 0)
        {
            return StepOutcome.Failed($"Skript {StepKey} mit Exit-Code {result.ExitCode} beendet (Details im Worker-Log).", "script-exit-code");
        }

        return Map(result.StandardOutput, input.InitialPassword);
    }

    /// <summary>Maps the script's stdout JSON to an outcome; never trusts it to be secret-free.</summary>
    public StepOutcome Map(string stdout, string? secret = null)
    {
        StepScriptOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<StepScriptOutput>(LastJsonLine(stdout), StepScriptJson.Options);
        }
        catch (JsonException)
        {
            output = null;
        }

        if (output is null || string.IsNullOrWhiteSpace(output.Status))
        {
            return StepOutcome.Failed($"Skript {StepKey} lieferte keine gültige JSON-Ausgabe.", "invalid-output");
        }

        var reason = Clean(output.Reason, secret);
        var outputJson = output.Output is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element
            ? Redact(element.GetRawText(), secret)
            : null;

        if (output.DryRun || Mode == IntegrationMode.DryRun)
        {
            var planned = output.PlannedActions.Select(a => Clean(a, secret)).Where(a => a is not null).ToList();
            if (planned.Count > 0 && output.Status is "done" or "needsInput")
            {
                return StepOutcome.NeedsInput(Truncate("Dry-Run – würde: " + string.Join("; ", planned), MaxReasonLength), "dry-run");
            }
        }

        // Codes are never free text: a malformed one is replaced, a missing one marked (DECISIONS X10).
        var code = string.IsNullOrEmpty(output.Code) ? "unspecified"
            : ReasonCodes.IsValid(output.Code) ? output.Code
            : "invalid-code";
        return output.Status switch
        {
            "done" => StepOutcome.Done(reason, outputJson, output.DirectoryObjectGuid),
            "waiting" => StepOutcome.Waiting(reason ?? "Vorbedingung noch nicht erfüllt.", code),
            "failed" => StepOutcome.Failed(reason ?? $"Skript {StepKey} meldet einen Fehler.", code),
            "needsInput" => StepOutcome.NeedsInput(reason ?? "Eingabe erforderlich.", code),
            "manualTask" => StepOutcome.ManualTask(reason ?? "Manuelle Aufgabe.", code),
            "skipped" => StepOutcome.Skipped(reason ?? "Übersprungen.", code),
            _ => StepOutcome.Failed($"Skript {StepKey} meldete unbekannten Status.", "invalid-output"),
        };
    }

    private static string LastJsonLine(string stdout)
    {
        // Scripts write exactly one JSON object; tolerate stray lines before it.
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(l => l.StartsWith('{')) ?? "";
    }

    private static string? Clean(string? text, string? secret) =>
        string.IsNullOrWhiteSpace(text) ? null : Truncate(Redact(text.Trim(), secret), MaxReasonLength);

    private static string Redact(string text, string? secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "***", StringComparison.Ordinal);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + " …";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script {StepKey} wrote to stderr: {Stderr}")]
    private static partial void LogStderr(ILogger logger, string stepKey, string stderr);
}
