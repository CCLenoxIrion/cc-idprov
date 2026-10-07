using System.Text.Json;
using System.Text.Json.Serialization;
using Onboarding.Core.Configuration;
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
    public string? DirectoryObjectSid { get; init; }
    public required ScriptConfig Config { get; init; }
    public ScriptLogonScript? LogonScript { get; init; }

    /// <summary>Cloud steps only (phase 4b).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScriptCloud? Cloud { get; init; }

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

    /// <summary>Home folder rights (X17); the file server endpoint accepts only allowlisted values.</summary>
    public required ScriptHomeAcl Home { get; init; }
}

public sealed class ScriptHomeAcl
{
    public required string UserRight { get; init; }
    public required IReadOnlyList<ScriptHomeAce> AdditionalAces { get; init; }
}

public sealed class ScriptHomeAce
{
    public required string Principal { get; init; }
    public required string Right { get; init; }
}

public sealed class ScriptJeaConfig
{
    /// <summary>File server with home folders and shares (GlobalConfig.Home.Server).</summary>
    public required string HomeComputer { get; init; }
    public required string HomeConfigurationName { get; init; }

    /// <summary>Domain controller for logon scripts (GlobalConfig.LogonScript.Server).</summary>
    public required string LogonComputer { get; init; }
    public required string LogonConfigurationName { get; init; }
    public required string SyncComputer { get; init; }
    public required string SyncConfigurationName { get; init; }
}

/// <summary>Input of the cloud steps. Values come from the request's configuration snapshot.</summary>
public sealed class ScriptCloud
{
    public required ScriptCloudAuth Auth { get; init; }
    public required string Upn { get; init; }
    public required string UsageLocation { get; init; }
    public required string LicenseMode { get; init; }
    public required IReadOnlyList<string> Skus { get; init; }
    public required IReadOnlyList<string> DisabledServicePlans { get; init; }
    public string? PhoneE164 { get; init; }
    public required bool OneWinNativeOutlookEnabled { get; init; }
    public required IReadOnlyList<ScriptSharedMailbox> SharedMailboxes { get; init; }
    public required ScriptTeams Teams { get; init; }

    /// <summary>Return a ManualTask with the command instead of connecting (DECISIONS X13).</summary>
    public required bool ManualOnly { get; init; }
}

/// <summary>Identifiers of the app registration – no secret; the key stays in the certificate store.</summary>
public sealed class ScriptCloudAuth
{
    public required string TenantId { get; init; }
    public required string AppId { get; init; }
    public required string CertificateThumbprint { get; init; }
    public required string Organization { get; init; }
}

public sealed class ScriptSharedMailbox
{
    public required string Mailbox { get; init; }
    public required bool FullAccess { get; init; }
    public required bool AutoMapping { get; init; }
    public required bool SendAs { get; init; }
}

public sealed class ScriptTeams
{
    public required string VoiceRoutingPolicy { get; init; }
    public required string VoicemailPolicy { get; init; }
    public required string PromptLanguage { get; init; }
    public required string PhoneNumberType { get; init; }
    public required bool Voicemail { get; init; }
    public required ScriptForward Forward { get; init; }
}

public sealed class ScriptForward
{
    public required bool Enabled { get; init; }
    public required int DelaySeconds { get; init; }
    public required string TargetType { get; init; }
    public required string Target { get; init; }
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

    /// <summary>objectSid (SDDL) of the account, from AD.CreateUser (DECISIONS X14).</summary>
    public string? DirectoryObjectSid { get; set; }
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

    /// <summary>
    /// Longer limits per step-key prefix, e.g. <c>"Teams.": "00:10:00"</c> (the MicrosoftTeams
    /// module loads slowly). The longest matching prefix wins.
    /// </summary>
    public Dictionary<string, TimeSpan> TimeoutOverrides { get; set; } = new(StringComparer.Ordinal);

    public string HomeConfigurationName { get; set; } = "CC.Onboarding";
    public string LogonConfigurationName { get; set; } = "CC.Onboarding.Logon";
    public string SyncConfigurationName { get; set; } = "CC.Onboarding.Sync";

    public TimeSpan TimeoutFor(string stepKey) =>
        TimeoutOverrides
            .Where(o => stepKey.StartsWith(o.Key, StringComparison.Ordinal))
            .OrderByDescending(o => o.Key.Length)
            .Select(o => (TimeSpan?)o.Value)
            .FirstOrDefault() ?? Timeout;

    /// <summary>Longest possible script run (base timeout or any override).</summary>
    public TimeSpan MaxTimeout => TimeoutOverrides.Values.Append(Timeout).Max();

    /// <summary>Throws a configuration error for non-positive limits or prefixes matching no step.</summary>
    public void Validate(IEnumerable<string> stepKeys)
    {
        ArgumentNullException.ThrowIfNull(stepKeys);
        var keys = stepKeys.ToList();
        if (Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Integrations:Scripts:Timeout must be positive.");
        }

        foreach (var (prefix, limit) in TimeoutOverrides)
        {
            if (limit <= TimeSpan.Zero)
            {
                throw new InvalidOperationException($"Integrations:Scripts:TimeoutOverrides '{prefix}' must be positive.");
            }

            if (prefix.Length == 0 || !keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Integrations:Scripts:TimeoutOverrides '{prefix}' matches no step key.");
            }
        }
    }
}

public static class StepScriptJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <summary>Builds the stdin payload for a step. Paths are deliberately not included.</summary>
    public static StepScriptInput BuildInput(StepContext context, string stepKey, bool dryRun, ScriptOptions options, CloudOptions? cloud = null)
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
            DirectoryObjectSid = context.DirectoryObjectSid,
            Config = new ScriptConfig
            {
                RequestIdAttribute = global.RequestIdAttribute,
                Groups = context.Snapshot.Department.AdGroups,
                Jea = new ScriptJeaConfig
                {
                    HomeComputer = global.Home.Server,
                    HomeConfigurationName = options.HomeConfigurationName,
                    LogonComputer = global.LogonScript.Server,
                    LogonConfigurationName = options.LogonConfigurationName,
                    SyncComputer = global.EntraConnectServer,
                    SyncConfigurationName = options.SyncConfigurationName,
                },
                Home = BuildHome(context),
            },
            LogonScript = logon,
            Cloud = cloud is not null && StepGroups.GroupOf(stepKey) == StepGroups.Cloud ? BuildCloud(context, stepKey, cloud) : null,
            InitialPassword = password?.Reveal(),
        };
    }

    private static ScriptHomeAcl BuildHome(StepContext context)
    {
        var acl = HomeAcl.Resolve(context.Snapshot.Global, context.Snapshot.Department);
        return new ScriptHomeAcl
        {
            UserRight = acl.UserRight.ToString(),
            AdditionalAces = acl.AdditionalAces.Select(a => new ScriptHomeAce { Principal = a.Principal, Right = a.Right.ToString() }).ToList(),
        };
    }

    private static ScriptCloud BuildCloud(StepContext context, string stepKey, CloudOptions cloud)
    {
        var global = context.Snapshot.Global;
        var department = context.Snapshot.Department;
        var forward = department.Teams.UnansweredForward;
        return new ScriptCloud
        {
            Auth = new ScriptCloudAuth
            {
                TenantId = cloud.TenantId,
                AppId = cloud.AppId,
                CertificateThumbprint = cloud.CertificateThumbprint,
                Organization = cloud.ExchangeOrganization,
            },
            Upn = context.Identity.UserPrincipalName,
            UsageLocation = global.UsageLocation,
            LicenseMode = global.LicenseMode.ToString(),
            Skus = StepValues.Skus(context),
            DisabledServicePlans = department.Licenses.DisabledServicePlans,
            PhoneE164 = context.Identity.PhoneE164,
            OneWinNativeOutlookEnabled = global.OneWinNativeOutlookEnabled,
            SharedMailboxes = department.SharedMailboxes
                .Select(m => new ScriptSharedMailbox { Mailbox = m.Mailbox, FullAccess = m.FullAccess, AutoMapping = m.AutoMapping, SendAs = m.SendAs })
                .ToList(),
            Teams = new ScriptTeams
            {
                VoiceRoutingPolicy = global.Teams.VoiceRoutingPolicy,
                VoicemailPolicy = global.Teams.VoicemailPolicy,
                PromptLanguage = global.Teams.VoicemailPromptLanguage,
                PhoneNumberType = global.Teams.PhoneNumberType,
                Voicemail = department.Teams.Voicemail,
                Forward = new ScriptForward
                {
                    Enabled = forward.Enabled,
                    DelaySeconds = (int)forward.Delay.TotalSeconds,
                    TargetType = forward.TargetType,
                    Target = forward.Target,
                },
            },
            ManualOnly = cloud.ManualSteps.Contains(stepKey, StringComparer.Ordinal),
        };
    }
}
