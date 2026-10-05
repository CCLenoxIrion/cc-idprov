using System.Text.Json;
using Onboarding.Core.Configuration;
using Onboarding.Core.Steps;
using Onboarding.Steps.Execution;
using static Onboarding.Core.Steps.StepKeys;

namespace Onboarding.Steps.Fakes.World;

/// <summary>
/// Base for fake executors: applies injected faults, then runs the idempotent check-and-act
/// against <see cref="FakeWorld"/> under its lock.
/// </summary>
public abstract class FakeStepExecutor(FakeWorld world) : IStepExecutor
{
    public abstract string StepKey { get; }

    protected FakeWorld World => world;

    public async Task<StepOutcome> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        FakeFault? fault;
        lock (world.Sync)
        {
            fault = world.TakeFault(StepKey, context.Identity.SamAccountName);
        }

        switch (fault?.Mode)
        {
            case FakeFaultMode.Fail:
                return StepOutcome.Failed(fault.Message, "injected-fault");
            case FakeFaultMode.Wait:
                return StepOutcome.Waiting(fault.Message, "injected-fault");
            case FakeFaultMode.NeedsInput:
                return StepOutcome.NeedsInput(fault.Message, "injected-fault");
            case FakeFaultMode.ManualTask:
                return StepOutcome.ManualTask(fault.Message, "injected-fault");
            case FakeFaultMode.Throw:
                throw new InvalidOperationException(fault.Message);
            case FakeFaultMode.Hang:
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                break;
        }

        await Task.Yield();
        lock (world.Sync)
        {
            return Execute(context);
        }
    }

    protected abstract StepOutcome Execute(StepContext context);

    protected static string Json(object value) => JsonSerializer.Serialize(value);

    protected static string Sam(StepContext context) => context.Identity.SamAccountName;

    protected static string Upn(StepContext context) => context.Identity.UserPrincipalName;

    /// <summary>The request's own AD account, or null.</summary>
    protected FakeAdUser? OwnAccount(StepContext context) =>
        World.AdUsers.TryGetValue(Sam(context), out var user) &&
        (user.RequestId == context.RequestId || user.ObjectGuid == context.DirectoryObjectGuid)
            ? user
            : null;

    /// <summary>
    /// The cloud user. When the on-prem steps run for real (<see cref="FakeWorldOptions.DetachedFromOnPrem"/>),
    /// the fake cloud cannot rely on the fake sync and creates the user on first access.
    /// </summary>
    protected FakeCloudUser? CloudUser(StepContext context)
    {
        if (World.CloudUsers.TryGetValue(Upn(context), out var user))
        {
            return user;
        }

        if (!World.Options.DetachedFromOnPrem)
        {
            return null;
        }

        user = new FakeCloudUser { UserPrincipalName = Upn(context) };
        World.CloudUsers[user.UserPrincipalName] = user;
        return user;
    }

    /// <summary>Waiting until a named counter reaches the configured number of checks.</summary>
    protected bool StillDelayed(string name, StepContext context, int delayChecks) =>
        World.Next($"{name}|{Sam(context)}") <= delayChecks;
}

public sealed class FakeAdCreateUser(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => AdCreateUser;

    protected override StepOutcome Execute(StepContext context)
    {
        var identity = context.Identity;
        var global = context.Snapshot.Global;
        if (string.IsNullOrWhiteSpace(global.RequestIdAttribute))
        {
            return StepOutcome.Failed("RequestIdAttribute ist nicht konfiguriert.", "config-missing");
        }

        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["givenName"] = identity.GivenName,
            ["sn"] = identity.Surname,
            ["displayName"] = identity.DisplayName,
            ["mail"] = identity.Mail,
            ["userPrincipalName"] = identity.UserPrincipalName,
            ["department"] = identity.Department,
            ["company"] = identity.Company,
            ["manager"] = context.Input.ManagerObjectGuid.ToString(),
            ["scriptPath"] = identity.ScriptPath,
            ["ou"] = identity.OuDistinguishedName,
            [global.RequestIdAttribute] = context.RequestId.ToString(),
        };
        if (identity.TelephoneNumber is not null)
        {
            desired["telephoneNumber"] = identity.TelephoneNumber;
        }

        foreach (var (name, value) in identity.AdditionalAttributes)
        {
            desired[name] = value;
        }

        if (World.AdUsers.TryGetValue(identity.SamAccountName, out var existing))
        {
            if (OwnAccount(context) is null)
            {
                return StepOutcome.NeedsInput(
                    $"Ein Konto mit sAMAccountName '{identity.SamAccountName}' existiert bereits und gehört nicht zu diesem Auftrag.", "foreign-account");
            }

            // Idempotency check compares all attributes, not only existence (SPEC §7).
            var drift = desired.Where(d => existing.Attributes.GetValueOrDefault(d.Key) != d.Value).Select(d => d.Key).ToList();
            var proxiesDiffer = !existing.ProxyAddresses.SequenceEqual(identity.ProxyAddresses, StringComparer.Ordinal);
            if (drift.Count == 0 && !proxiesDiffer)
            {
                return StepOutcome.Done("Konto existiert bereits mit allen Attributen.", Json(new { existing.ObjectGuid }), existing.ObjectGuid, existing.Sid);
            }

            foreach (var name in drift)
            {
                existing.Attributes[name] = desired[name];
            }

            existing.ProxyAddresses = [.. identity.ProxyAddresses];
            World.Wrote();
            return StepOutcome.Done($"Attribute korrigiert: {string.Join(", ", drift.Concat(proxiesDiffer ? ["proxyAddresses"] : []))}.",
                Json(new { existing.ObjectGuid }), existing.ObjectGuid, existing.Sid);
        }

        if (context.GetInitialPassword() is not { Length: > 0 })
        {
            return StepOutcome.Failed("Kein Startpasswort vorhanden.", "missing-password");
        }

        var user = new FakeAdUser
        {
            SamAccountName = identity.SamAccountName,
            RequestId = context.RequestId,
            Enabled = false,
            PasswordSet = true,
            MustChangePassword = true,
            ProxyAddresses = [.. identity.ProxyAddresses],
        };
        foreach (var (name, value) in desired)
        {
            user.Attributes[name] = value;
        }

        World.AdUsers[identity.SamAccountName] = user;
        World.Wrote();
        return StepOutcome.Done("Konto deaktiviert angelegt, Kennwortänderung bei Anmeldung erzwungen.",
            Json(new { user.ObjectGuid, Ou = identity.OuDistinguishedName }), user.ObjectGuid, user.Sid);
    }
}

public sealed class FakeAdGroups(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => AdGroups;

    protected override StepOutcome Execute(StepContext context)
    {
        if (OwnAccount(context) is not { } user)
        {
            return StepOutcome.Failed("Konto nicht gefunden.", "account-not-found");
        }

        var missing = context.Snapshot.Department.AdGroups.Where(g => !user.Groups.Contains(g)).ToList();
        foreach (var group in missing)
        {
            user.Groups.Add(group);
            World.Wrote();
        }

        return StepOutcome.Done(missing.Count == 0 ? "Alle Gruppen bereits zugewiesen." : $"Hinzugefügt: {string.Join(", ", missing)}.",
            Json(new { Groups = user.Groups.Order(StringComparer.OrdinalIgnoreCase) }));
    }
}

public sealed class FakeHomeFolder(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => HomeFolder;

    protected override StepOutcome Execute(StepContext context)
    {
        var path = $"{context.Snapshot.Global.Home.LocalRoot.TrimEnd('\\')}\\{Sam(context)}";
        if (World.HomeFolders.ContainsKey(path))
        {
            return StepOutcome.Done("Ordner existiert bereits.", Json(new { Path = path }));
        }

        World.HomeFolders[path] = $"{Sam(context)}:Modify; Administrators:FullControl; SYSTEM:FullControl";
        World.Wrote();
        return StepOutcome.Done("Ordner angelegt.", Json(new { Path = path }));
    }
}

public sealed class FakeHomeShare(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => HomeShare;

    protected override StepOutcome Execute(StepContext context)
    {
        var home = context.Snapshot.Global.Home;
        var path = $"{home.LocalRoot.TrimEnd('\\')}\\{Sam(context)}";
        if (!World.HomeFolders.ContainsKey(path))
        {
            return StepOutcome.Failed("Home-Ordner fehlt.", "home-folder-missing");
        }

        var name = StepValues.Render(home.ShareNamePattern, context);
        if (World.Shares.TryGetValue(name, out var existing))
        {
            return existing == path
                ? StepOutcome.Done("Freigabe existiert bereits.", Json(new { Share = name }))
                : StepOutcome.NeedsInput($"Freigabe '{name}' existiert bereits mit anderem Pfad.", "share-path-mismatch");
        }

        World.Shares[name] = path;
        World.Wrote();
        return StepOutcome.Done("Freigabe angelegt.", Json(new { Share = name }));
    }
}

/// <summary>Logon script with hash-based idempotency (DECISIONS L3).</summary>
public sealed class FakeLogonScript(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => LogonScript;

    protected override StepOutcome Execute(StepContext context)
    {
        var file = LogonScriptFile.For(context);
        var output = Json(new { file.Path, Sha256 = file.Sha256, Bytes = file.Content.Length });
        if (World.Files.TryGetValue(file.Path, out var existing))
        {
            var existingHash = LogonScriptFile.Sha256Of(existing);
            if (existingHash == file.Sha256)
            {
                return StepOutcome.Done($"Datei vorhanden, SHA-256 {file.Sha256}.", output);
            }

            if (!context.ForceRequested)
            {
                return StepOutcome.NeedsInput(
                    $"{file.Path} existiert mit anderem Inhalt (SHA-256 {existingHash}, erwartet {file.Sha256}); " +
                    "Datei wurde manuell angelegt oder geändert. Überschreiben nur per Admin-Aktion.", "logon-script-modified");
            }
        }

        World.Files[file.Path] = file.Content;
        World.Wrote();
        return StepOutcome.Done(
            existing is null ? $"Datei geschrieben, SHA-256 {file.Sha256}." : $"Datei überschrieben, SHA-256 {file.Sha256}.",
            output);
    }
}

/// <summary>Delta sync: copies AD state to the cloud users (both sync steps).</summary>
public sealed class FakeSync(FakeWorld world, string stepKey) : FakeStepExecutor(world)
{
    public override string StepKey => stepKey;

    protected override StepOutcome Execute(StepContext context)
    {
        if (World.Next($"busy|{StepKey}|{Sam(context)}") <= World.Options.SyncBusyCount)
        {
            return StepOutcome.Waiting("Synchronisierung läuft bereits (busy).", "sync-busy");
        }

        if (OwnAccount(context) is not { } user)
        {
            return StepOutcome.Failed("Konto nicht gefunden.", "account-not-found");
        }

        if (!World.CloudUsers.TryGetValue(Upn(context), out var cloud))
        {
            cloud = new FakeCloudUser { UserPrincipalName = Upn(context) };
            World.CloudUsers[cloud.UserPrincipalName] = cloud;
        }

        cloud.AccountEnabled = user.Enabled;
        World.Wrote();
        return StepOutcome.Done($"Delta-Sync auf {context.Snapshot.Global.EntraConnectServer} ausgelöst.");
    }
}

public sealed class FakeEntraWaitUser(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => EntraWaitUser;

    protected override StepOutcome Execute(StepContext context) =>
        CloudUser(context) is null || StillDelayed(StepKey, context, World.Options.EntraUserDelayChecks)
            ? StepOutcome.Waiting("Benutzer in Entra noch nicht sichtbar.", "entra-user-pending")
            : StepOutcome.Done("Benutzer in Entra gefunden.");
}

public sealed class FakeEntraUsageLocation(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => EntraUsageLocation;

    protected override StepOutcome Execute(StepContext context)
    {
        if (CloudUser(context) is not { } cloud)
        {
            return StepOutcome.Failed("Benutzer in Entra nicht gefunden.", "entra-user-not-found");
        }

        var location = context.Snapshot.Global.UsageLocation;
        if (cloud.UsageLocation == location)
        {
            return StepOutcome.Done($"usageLocation bereits {location}.");
        }

        cloud.UsageLocation = location;
        World.Wrote();
        return StepOutcome.Done($"usageLocation = {location}.");
    }
}

public sealed class FakeEntraAssignLicense(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => EntraAssignLicense;

    protected override StepOutcome Execute(StepContext context)
    {
        if (context.Snapshot.Global.LicenseMode == LicenseMode.Group)
        {
            return StepOutcome.Skipped("LicenseMode = Group.", "license-mode-group");
        }

        if (CloudUser(context) is not { UsageLocation: not null } cloud)
        {
            return StepOutcome.Failed("Benutzer ohne usageLocation.", "usage-location-missing");
        }

        var missing = StepValues.Skus(context).Where(s => !cloud.Licenses.Contains(s)).ToList();
        var unavailable = missing.Where(s => World.FreeLicenses.GetValueOrDefault(s) <= 0).ToList();
        if (unavailable.Count > 0)
        {
            return StepOutcome.Failed($"Keine freien Lizenzen für {string.Join(", ", unavailable)}.", "no-free-license");
        }

        foreach (var sku in missing)
        {
            World.FreeLicenses[sku]--;
            cloud.Licenses.Add(sku);
            World.Wrote();
        }

        return StepOutcome.Done(missing.Count == 0 ? "Lizenzen bereits zugewiesen." : $"Zugewiesen: {string.Join(", ", missing)}.",
            Json(new { Skus = cloud.Licenses.Order(StringComparer.OrdinalIgnoreCase) }));
    }
}

public sealed class FakeEntraWaitLicense(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => EntraWaitLicense;

    protected override StepOutcome Execute(StepContext context)
    {
        var expected = context.Snapshot.Global.LicenseMode == LicenseMode.Direct ? StepValues.Skus(context) : [];
        if (CloudUser(context) is not { } cloud || expected.Any(s => !cloud.Licenses.Contains(s)))
        {
            return StepOutcome.Failed("Lizenzen sind nicht zugewiesen.", "license-not-assigned");
        }

        return StillDelayed(StepKey, context, World.Options.LicenseDelayChecks)
            ? StepOutcome.Waiting("Lizenz noch nicht aktiv.", "license-pending")
            : StepOutcome.Done("Lizenzen aktiv.");
    }
}

public sealed class FakeExoWaitMailbox(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => ExoWaitMailbox;

    protected override StepOutcome Execute(StepContext context)
    {
        if (CloudUser(context) is not { } cloud)
        {
            return StepOutcome.Failed("Benutzer in Entra nicht gefunden.", "entra-user-not-found");
        }

        if (!cloud.HasMailbox && StillDelayed(StepKey, context, World.Options.MailboxDelayChecks))
        {
            return StepOutcome.Waiting("Postfach noch nicht bereitgestellt.", "mailbox-pending");
        }

        cloud.HasMailbox = true;
        return StepOutcome.Done("Postfach vorhanden.");
    }
}

public sealed class FakeExoDisableNewOutlook(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => ExoDisableNewOutlook;

    protected override StepOutcome Execute(StepContext context)
    {
        if (CloudUser(context) is not { HasMailbox: true } cloud)
        {
            return StepOutcome.Failed("Postfach nicht gefunden.", "mailbox-not-found");
        }

        var desired = context.Snapshot.Global.OneWinNativeOutlookEnabled;
        if (cloud.OneWinNativeOutlookEnabled == desired)
        {
            return StepOutcome.Done($"OneWinNativeOutlookEnabled bereits {desired}.");
        }

        cloud.OneWinNativeOutlookEnabled = desired;
        World.Wrote();
        return StepOutcome.Done($"OneWinNativeOutlookEnabled = {desired}.");
    }
}

public sealed class FakeExoSharedMailboxes(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => ExoSharedMailboxes;

    protected override StepOutcome Execute(StepContext context)
    {
        if (CloudUser(context) is not { HasMailbox: true } cloud)
        {
            return StepOutcome.Failed("Postfach nicht gefunden.", "mailbox-not-found");
        }

        var added = new List<string>();
        foreach (var mailbox in context.Snapshot.Department.SharedMailboxes)
        {
            var rights = new List<string>();
            if (mailbox.FullAccess)
            {
                rights.Add($"{mailbox.Mailbox}:FullAccess:AutoMapping={mailbox.AutoMapping}");
            }

            if (mailbox.SendAs)
            {
                rights.Add($"{mailbox.Mailbox}:SendAs");
            }

            foreach (var right in rights.Where(cloud.MailboxPermissions.Add))
            {
                added.Add(right);
                World.Wrote();
            }
        }

        return StepOutcome.Done(added.Count == 0 ? "Berechtigungen bereits vorhanden." : $"Hinzugefügt: {string.Join(", ", added)}.");
    }
}

public sealed class FakeTeamsWaitUser(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => TeamsWaitUser;

    protected override StepOutcome Execute(StepContext context)
    {
        if (CloudUser(context) is not { } cloud)
        {
            return StepOutcome.Failed("Benutzer in Entra nicht gefunden.", "entra-user-not-found");
        }

        if (!cloud.TeamsUser && StillDelayed(StepKey, context, World.Options.TeamsUserDelayChecks))
        {
            return StepOutcome.Waiting("Teams-Benutzer mit Phone-Plan noch nicht verfügbar.", "teams-user-pending");
        }

        cloud.TeamsUser = true;
        return StepOutcome.Done("Teams-Benutzer verfügbar.");
    }
}

public sealed class FakeTeamsPhone(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => TeamsPhone;

    protected override StepOutcome Execute(StepContext context)
    {
        var number = context.Identity.PhoneE164;
        if (number is null)
        {
            return StepOutcome.Skipped("Keine Durchwahl.", "no-extension");
        }

        if (CloudUser(context) is not { TeamsUser: true } cloud)
        {
            return StepOutcome.Failed("Teams-Benutzer nicht gefunden.", "teams-user-not-found");
        }

        if (cloud.PhoneNumber == number)
        {
            return StepOutcome.Done($"Nummer {number} bereits zugewiesen.");
        }

        if (World.CloudUsers.Values.Any(u => u != cloud && u.PhoneNumber == number))
        {
            return StepOutcome.Failed($"Nummer {number} ist bereits einem anderen Benutzer zugewiesen.", "number-in-use");
        }

        cloud.PhoneNumber = number;
        World.Wrote();
        return StepOutcome.Done($"Nummer {number} ({context.Snapshot.Global.Teams.PhoneNumberType}) zugewiesen.");
    }
}

/// <summary>Teams settings that are a single value per user (voice routing, voicemail, forwarding).</summary>
public sealed class FakeTeamsSetting(
    FakeWorld world,
    string stepKey,
    Func<StepContext, string> desired,
    Func<FakeCloudUser, string?> get,
    Action<FakeCloudUser, string> set) : FakeStepExecutor(world)
{
    public override string StepKey => stepKey;

    protected override StepOutcome Execute(StepContext context)
    {
        if (CloudUser(context) is not { PhoneNumber: not null } cloud)
        {
            return StepOutcome.Failed("Keine Nummer zugewiesen.", "number-not-assigned");
        }

        var value = desired(context);
        if (get(cloud) == value)
        {
            return StepOutcome.Done($"Bereits gesetzt: {value}.");
        }

        set(cloud, value);
        World.Wrote();
        return StepOutcome.Done($"Gesetzt: {value}.");
    }
}

public sealed class FakeAdEnable(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => AdEnable;

    protected override StepOutcome Execute(StepContext context)
    {
        if (OwnAccount(context) is not { } user)
        {
            return StepOutcome.Failed("Konto nicht gefunden.", "account-not-found");
        }

        if (user.Enabled)
        {
            return StepOutcome.Done("Konto bereits aktiviert.");
        }

        user.Enabled = true;
        World.Wrote();
        return StepOutcome.Done("Konto aktiviert.");
    }
}

public sealed class FakeEntraWaitEnabled(FakeWorld world) : FakeStepExecutor(world)
{
    public override string StepKey => EntraWaitEnabled;

    protected override StepOutcome Execute(StepContext context)
    {
        if (World.Options.DetachedFromOnPrem && CloudUser(context) is { } detached)
        {
            // Runs only after AD.Enable and the post-enable sync (both real in this mode).
            detached.AccountEnabled = true;
        }

        return CloudUser(context) is not { AccountEnabled: true } || StillDelayed(StepKey, context, World.Options.EnabledDelayChecks)
            ? StepOutcome.Waiting("accountEnabled in Entra noch false.", "entra-enable-pending")
            : StepOutcome.Done("accountEnabled = true in Entra.");
    }
}

/// <summary>Creates one fake executor per onboarding step key.</summary>
public static class FakeStepExecutors
{
    public static IReadOnlyList<IStepExecutor> Create(FakeWorld world) =>
    [
        new FakeAdCreateUser(world),
        new FakeAdGroups(world),
        new FakeHomeFolder(world),
        new FakeHomeShare(world),
        new FakeLogonScript(world),
        new FakeSync(world, SyncDelta),
        new FakeEntraWaitUser(world),
        new FakeEntraUsageLocation(world),
        new FakeEntraAssignLicense(world),
        new FakeEntraWaitLicense(world),
        new FakeExoWaitMailbox(world),
        new FakeExoDisableNewOutlook(world),
        new FakeExoSharedMailboxes(world),
        new FakeTeamsWaitUser(world),
        new FakeTeamsPhone(world),
        new FakeTeamsSetting(world, TeamsVoiceRouting,
            c => c.Snapshot.Global.Teams.VoiceRoutingPolicy, u => u.VoiceRoutingPolicy, (u, v) => u.VoiceRoutingPolicy = v),
        new FakeTeamsSetting(world, TeamsVoicemail,
            c => $"{c.Snapshot.Global.Teams.VoicemailPolicy}|{c.Snapshot.Global.Teams.VoicemailPromptLanguage}",
            u => u.VoicemailPolicy, (u, v) => u.VoicemailPolicy = v),
        new FakeTeamsSetting(world, TeamsForwarding,
            c => $"{c.Snapshot.Department.Teams.UnansweredForward.TargetType}:{c.Snapshot.Department.Teams.UnansweredForward.Target}" +
                 $"@{c.Snapshot.Department.Teams.UnansweredForward.Delay}",
            u => u.Forwarding, (u, v) => u.Forwarding = v),
        new FakeAdEnable(world),
        new FakeSync(world, SyncDeltaAfterEnable),
        new FakeEntraWaitEnabled(world),
    ];
}
