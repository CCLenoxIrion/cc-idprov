using Onboarding.Core.Domain;

namespace Onboarding.Core.Configuration;

/// <summary>Department settings (SPEC §4.3), independent of the area.</summary>
public sealed class DepartmentConfig : IVersioned
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public List<string> AdGroups { get; set; } = [];
    public LicenseConfig Licenses { get; set; } = new();
    public List<SharedMailboxConfig> SharedMailboxes { get; set; } = [];
    public LogonScriptTemplate LogonScript { get; set; } = new();
    public DepartmentTeamsConfig Teams { get; set; } = new();
    public long Version { get; set; }
}

public sealed class LicenseConfig
{
    public List<string> SkuPartNumbers { get; set; } = [];
    public List<string> SkuPartNumbersIfPhone { get; set; } = [];
    public List<string> DisabledServicePlans { get; set; } = [];
    public string? LicenseGroup { get; set; }
}

public sealed class SharedMailboxConfig
{
    public string Mailbox { get; set; } = "";
    public bool FullAccess { get; set; }
    public bool AutoMapping { get; set; }
    public bool SendAs { get; set; }
}

/// <summary>Department logon script template (SPEC §4.4).</summary>
public sealed class LogonScriptTemplate
{
    /// <summary>Drive letters to disconnect, in output order.</summary>
    public List<string> DisconnectDrives { get; set; } = [];

    public List<DriveMapping> ConnectDrives { get; set; } = [];

    /// <summary>Additional verbatim lines (placeholders allowed).</summary>
    public List<string> ExtraLines { get; set; } = [];
}

public sealed class DriveMapping
{
    public string Letter { get; set; } = "";

    /// <summary>UNC path; placeholders such as <c>{homeUnc}</c> allowed.</summary>
    public string Unc { get; set; } = "";
}

public sealed class DepartmentTeamsConfig
{
    public bool AssignPhone { get; set; }
    public bool Voicemail { get; set; }
    public UnansweredForwardConfig UnansweredForward { get; set; } = new();
}

public sealed class UnansweredForwardConfig
{
    public bool Enabled { get; set; }
    public TimeSpan Delay { get; set; }
    public string TargetType { get; set; } = "";
    public string Target { get; set; } = "";
}
