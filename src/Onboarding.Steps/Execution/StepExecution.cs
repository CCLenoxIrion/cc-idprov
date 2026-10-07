using System.Security.Cryptography;
using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Core.LogonScripts;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;
using Onboarding.Core.Steps;

namespace Onboarding.Steps.Execution;

/// <summary>
/// Executes one step key. Implementations are idempotent: they first check whether the target
/// state is already reached (→ Done) and only then act (SPEC §6). Outcomes never contain secrets.
/// </summary>
public interface IStepExecutor
{
    string StepKey { get; }

    Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken);
}

/// <summary>Everything an executor may know about the request. Read-only.</summary>
public sealed class StepContext
{
    private readonly Func<SecretString?> _initialPassword;

    public StepContext(
        Guid requestId,
        PersonInput input,
        DerivedIdentity identity,
        RequestConfigSnapshot snapshot,
        bool forceRequested,
        Guid? directoryObjectGuid,
        Func<SecretString?> initialPassword,
        string? directoryObjectSid = null)
    {
        DirectoryObjectSid = directoryObjectSid;
        RequestId = requestId;
        Input = input;
        Identity = identity;
        Snapshot = snapshot;
        ForceRequested = forceRequested;
        DirectoryObjectGuid = directoryObjectGuid;
        _initialPassword = initialPassword;
    }

    public Guid RequestId { get; }
    public PersonInput Input { get; }
    public DerivedIdentity Identity { get; }
    public RequestConfigSnapshot Snapshot { get; }

    /// <summary>"Überschreiben" was requested for this run (DECISIONS L3).</summary>
    public bool ForceRequested { get; }

    public Guid? DirectoryObjectGuid { get; }

    /// <summary>objectSid of the request's AD account, once known (DECISIONS X14).</summary>
    public string? DirectoryObjectSid { get; }

    public bool HasExtension => !string.IsNullOrWhiteSpace(Input.Extension);

    /// <summary>Decrypts the initial password on demand (only <c>AD.CreateUser</c>). Null if none is stored.</summary>
    public SecretString? GetInitialPassword() => _initialPassword();
}

/// <summary>Looks up the executor for a step key.</summary>
public sealed class StepExecutorRegistry
{
    private readonly Dictionary<string, IStepExecutor> _executors;

    public StepExecutorRegistry(IEnumerable<IStepExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _executors = executors.ToDictionary(e => e.StepKey, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Keys => _executors.Keys;

    public IStepExecutor Get(string stepKey) =>
        _executors.TryGetValue(stepKey, out var executor)
            ? executor
            : throw new InvalidOperationException($"No executor registered for step {stepKey}.");
}

/// <summary>Logon script content and target path for a request (shared by fake and real executors).</summary>
public sealed record LogonScriptFile(string Path, byte[] Content)
{
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Content));

    public static LogonScriptFile For(StepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var department = context.Snapshot.Department;
        var content = LogonScriptGenerator.Generate(
            department.LogonScript,
            new LogonScriptValues(context.Identity.SamAccountName, context.Identity.HomeUnc, department.Name));
        var directory = context.Snapshot.Global.LogonScript.Path.TrimEnd('\\', '/');
        return new LogonScriptFile($"{directory}\\{context.Identity.ScriptPath}", content);
    }

    public static string Sha256Of(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

/// <summary>Rendering helpers for config patterns that need the request's values.</summary>
public static class StepValues
{
    public static string Render(string pattern, StepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PlaceholderRenderer.Render(pattern, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Placeholders.Sam] = context.Identity.SamAccountName,
            [Placeholders.MailLocal] = IdentityValidator.LocalPart(context.Identity.Mail),
            [Placeholders.HomeUnc] = context.Identity.HomeUnc,
            [Placeholders.Department] = context.Snapshot.Department.Name,
        });
    }

    /// <summary>SKUs to assign: department SKUs plus phone SKUs if an extension is set (DECISIONS T1).</summary>
    public static IReadOnlyList<string> Skus(StepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var licenses = context.Snapshot.Department.Licenses;
        return licenses.SkuPartNumbers
            .Concat(context.HasExtension ? licenses.SkuPartNumbersIfPhone : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
