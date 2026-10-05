using System.Text.Json;
using System.Text.Json.Serialization;
using Onboarding.Core.Security;
using Onboarding.Core.Steps;
using Onboarding.Steps.Execution;

namespace Onboarding.Steps.Scripts;

/// <summary>
/// JSON written to the script's stdin (DECISIONS X3). Contains no paths – file system locations
/// are known only to the JEA endpoints. <see cref="InitialPassword"/> is set for AD.CreateUser
/// only and never logged.
/// </summary>
public sealed class StepScriptInput
{
    public required string Step { get; init; }
    public required Guid RequestId { get; init; }
    public required bool DryRun { get; init; }
    public required bool Force { get; init; }
    public required ScriptIdentity Identity { get; init; }
    public required Guid ManagerObjectGuid { get; init; }
    public Guid? DirectoryObjectGuid { get; init; }
    public required ScriptConfig Config { get; init; }
    public ScriptLogonScript? LogonScript { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InitialPassword { get; init; }

    public override string ToString() => $"StepScriptInput({Step}, {RequestId})";
}

public sealed class ScriptIdentity
{
    public required string Sam { get; init; }
    public required string Upn { get; init; }
    public required string Mail { get; init; }
    public required string GivenName { get; init; }
    public required string Sn { get; init; }
    public required string DisplayName { get; init; }
    public required string Department { get; init; }
    public required string Company { get; init; }
    public required string OuDn { get; init; }
    public required string ScriptPath { get; init; }
    public string? TelephoneNumber { get; init; }
    public required IReadOnlyList<string> ProxyAddresses { get; init; }
    public required IReadOnlyDictionary<string, string> AdditionalAttributes { get; init; }
}

public sealed class ScriptConfig
{
    public required string RequestIdAttribute { get; init; }
    public required IReadOnlyList<string> Groups { get; init; }
    public required ScriptJeaConfig Jea { get; init; }
}

public sealed class ScriptJeaConfig
{
    public required string DcComputer { get; init; }
    public required string DcConfigurationName { get; init; }
    public required string SyncComputer { get; init; }
    public required string SyncConfigurationName { get; init; }
}

public sealed class ScriptLogonScript
{
    public required string ContentBase64 { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>JSON the script writes to stdout.</summary>
public sealed class StepScriptOutput
{
    public string Status { get; set; } = "";

    /// <summary>Fixed reason code (DECISIONS X10); required for every status except done.</summary>
    public string? Code { get; set; }

    public string? Reason { get; set; }
    public JsonElement? Output { get; set; }
    public Guid? DirectoryObjectGuid { get; set; }
    public bool DryRun { get; set; }
    public List<string> PlannedActions { get; set; } = [];
}

/// <summary>Configuration of the script executor (<c>Integrations:Scripts</c>).</summary>
public sealed class ScriptOptions
{
    /// <summary>pwsh executable (PowerShell 7).</summary>
    public string PwshPath { get; set; } = "pwsh";

    /// <summary>Folder with <c>steps/&lt;StepKey&gt;.ps1</c>; writable by administrators only (DEPLOYMENT.md).</summary>
    public string ScriptsDirectory { get; set; } = "";

    /// <summary>Hard limit per script run; the process tree is killed afterwards.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(4);

    public string DcConfigurationName { get; set; } = "CC.Onboarding";
    public string SyncConfigurationName { get; set; } = "CC.Onboarding.Sync";
}

public static class StepScriptJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>Builds the stdin payload for a step. Paths are deliberately not included.</summary>
    public static StepScriptInput BuildInput(StepContext context, string stepKey, bool dryRun, ScriptOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        var identity = context.Identity;
        var global = context.Snapshot.Global;
        ScriptLogonScript? logon = null;
        if (stepKey == StepKeys.LogonScript)
        {
            var file = LogonScriptFile.For(context);
            logon = new ScriptLogonScript { ContentBase64 = Convert.ToBase64String(file.Content), Sha256 = file.Sha256 };
        }

        SecretString? password = stepKey == StepKeys.AdCreateUser ? context.GetInitialPassword() : null;
        return new StepScriptInput
        {
            Step = stepKey,
            RequestId = context.RequestId,
            DryRun = dryRun,
            Force = context.ForceRequested,
            Identity = new ScriptIdentity
            {
                Sam = identity.SamAccountName,
                Upn = identity.UserPrincipalName,
                Mail = identity.Mail,
                GivenName = identity.GivenName,
                Sn = identity.Surname,
                DisplayName = identity.DisplayName,
                Department = identity.Department,
                Company = identity.Company,
                OuDn = identity.OuDistinguishedName,
                ScriptPath = identity.ScriptPath,
                TelephoneNumber = identity.TelephoneNumber,
                ProxyAddresses = identity.ProxyAddresses,
                AdditionalAttributes = identity.AdditionalAttributes,
            },
            ManagerObjectGuid = context.Input.ManagerObjectGuid,
            DirectoryObjectGuid = context.DirectoryObjectGuid,
            Config = new ScriptConfig
            {
                RequestIdAttribute = global.RequestIdAttribute,
                Groups = context.Snapshot.Department.AdGroups,
                Jea = new ScriptJeaConfig
                {
                    DcComputer = global.Home.Server,
                    DcConfigurationName = options.DcConfigurationName,
                    SyncComputer = global.EntraConnectServer,
                    SyncConfigurationName = options.SyncConfigurationName,
                },
            },
            LogonScript = logon,
            InitialPassword = password?.Reveal(),
        };
    }
}
