namespace Onboarding.Steps.Fakes.World;

public enum FakeFaultMode
{
    /// <summary>Step returns Failed.</summary>
    Fail,

    /// <summary>Step returns Waiting.</summary>
    Wait,

    NeedsInput,
    ManualTask,

    /// <summary>Executor throws an exception.</summary>
    Throw,

    /// <summary>Executor never returns (until cancelled) – tests the execution timeout.</summary>
    Hang,
}

/// <summary>Injected misbehaviour for one step key (and optionally one sam).</summary>
public sealed class FakeFault
{
    public string StepKey { get; set; } = "";

    /// <summary>sAMAccountName, or "*" for every request.</summary>
    public string Sam { get; set; } = "*";

    public FakeFaultMode Mode { get; set; } = FakeFaultMode.Fail;

    /// <summary>How often the fault fires; 0 = always.</summary>
    public int Count { get; set; }

    public string Message { get; set; } = "Simulierter Fehler (Fake).";
}

/// <summary>Behaviour of the simulated systems (config section <c>FakeWorld</c>).</summary>
public sealed class FakeWorldOptions
{
    /// <summary>Waiting answers of Entra.WaitUser before the synced user is visible.</summary>
    public int EntraUserDelayChecks { get; set; } = 1;

    public int LicenseDelayChecks { get; set; } = 1;
    public int MailboxDelayChecks { get; set; } = 1;
    public int TeamsUserDelayChecks { get; set; } = 2;

    /// <summary>Waiting answers of Entra.WaitEnabled after the post-enable sync.</summary>
    public int EnabledDelayChecks { get; set; } = 1;

    /// <summary>Sync attempts answered with "busy" (per sync step and user).</summary>
    public int SyncBusyCount { get; set; }

    /// <summary>Free licenses per SKU part number.</summary>
    public Dictionary<string, int> FreeLicenses { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Accounts that already exist in AD and belong to nobody's request (collision tests).</summary>
    public List<string> ExistingSamAccountNames { get; set; } = [];

    public List<FakeFault> Faults { get; set; } = [];
}

public sealed class FakeAdUser
{
    public Guid ObjectGuid { get; init; } = Guid.NewGuid();
    public required string SamAccountName { get; init; }
    public Guid? RequestId { get; set; }
    public bool Enabled { get; set; }
    public bool PasswordSet { get; set; }
    public bool MustChangePassword { get; set; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ProxyAddresses { get; set; } = [];
    public HashSet<string> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FakeCloudUser
{
    public required string UserPrincipalName { get; init; }
    public bool AccountEnabled { get; set; }
    public string? UsageLocation { get; set; }
    public HashSet<string> Licenses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasMailbox { get; set; }
    public bool? OneWinNativeOutlookEnabled { get; set; }
    public HashSet<string> MailboxPermissions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool TeamsUser { get; set; }
    public string? PhoneNumber { get; set; }
    public string? VoiceRoutingPolicy { get; set; }
    public string? VoicemailPolicy { get; set; }
    public string? Forwarding { get; set; }
}

/// <summary>
/// Thread-safe in-memory stand-in for AD, file shares, NETLOGON, Entra, Exchange Online and
/// Teams. Nothing here touches a real system. State is lost when the worker restarts.
/// </summary>
public sealed class FakeWorld
{
    private readonly Dictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _faultsFired = new(StringComparer.OrdinalIgnoreCase);

    public FakeWorld(FakeWorldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        FreeLicenses = new Dictionary<string, int>(options.FreeLicenses, StringComparer.OrdinalIgnoreCase);
        foreach (var sam in options.ExistingSamAccountNames)
        {
            AdUsers[sam] = new FakeAdUser { SamAccountName = sam, Enabled = true, PasswordSet = true };
        }
    }

    public FakeWorldOptions Options { get; }

    /// <summary>Lock for all state; executors hold it for their whole check-and-act.</summary>
    public Lock Sync { get; } = new();

    public Dictionary<string, FakeAdUser> AdUsers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> HomeFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Shares { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FakeCloudUser> CloudUsers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> FreeLicenses { get; }

    /// <summary>Number of side-effecting operations, for idempotency tests (AK 2).</summary>
    public int WriteOperations { get; private set; }

    internal void Wrote() => WriteOperations++;

    /// <summary>Increments and returns a named counter (e.g. checks of a waiting step).</summary>
    internal int Next(string key)
    {
        _counters[key] = _counters.GetValueOrDefault(key) + 1;
        return _counters[key];
    }

    /// <summary>Returns the fault to apply now, if any (and counts it).</summary>
    internal FakeFault? TakeFault(string stepKey, string sam)
    {
        for (var i = 0; i < Options.Faults.Count; i++)
        {
            var fault = Options.Faults[i];
            if (!string.Equals(fault.StepKey, stepKey, StringComparison.Ordinal) ||
                (fault.Sam != "*" && !string.Equals(fault.Sam, sam, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var key = $"{i}|{sam}";
            var fired = _faultsFired.GetValueOrDefault(key);
            if (fault.Count > 0 && fired >= fault.Count)
            {
                continue;
            }

            _faultsFired[key] = fired + 1;
            return fault;
        }

        return null;
    }
}
