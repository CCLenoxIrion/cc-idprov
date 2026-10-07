namespace Onboarding.Core.Configuration;

/// <summary>
/// Global settings (SPEC §4.1 plus execution settings from §6). All values come from the
/// database; this type intentionally has no spec values as defaults.
/// </summary>
public sealed class GlobalConfig
{
    public string DomainFqdn { get; set; } = "";
    public string DomainNetBios { get; set; } = "";
    public string UpnSuffix { get; set; } = "";

    /// <summary>Pattern used to pre-fill the mail address, e.g. <c>{firstInitial}.{lastName}@example.com</c>.</summary>
    public string MailPattern { get; set; } = "";

    /// <summary>proxyAddresses templates; prefix case is kept verbatim (<c>SMTP:</c> = primary).</summary>
    public List<string> ProxyAddressTemplates { get; set; } = [];

    public DoctorTitleConfig DoctorTitle { get; set; } = new();
    public string PhonePrefixE164 { get; set; } = "";
    public string PhoneDisplayFormat { get; set; } = "";
    public HomeConfig Home { get; set; } = new();
    public LogonScriptLocationConfig LogonScript { get; set; } = new();
    public string EntraConnectServer { get; set; } = "";
    public string UsageLocation { get; set; } = "";
    public LicenseMode LicenseMode { get; set; } = LicenseMode.Direct;
    public bool OneWinNativeOutlookEnabled { get; set; }
    public TeamsGlobalConfig Teams { get; set; } = new();
    public string DisabledUsersOU { get; set; } = "";

    /// <summary>IANA time zone for business dates (entry date, <c>AD.Enable</c>).</summary>
    public string TimeZone { get; set; } = "";

    /// <summary>How long before 00:00 of the entry date the account may be enabled.</summary>
    public TimeSpan EnableLeadTime { get; set; }

    /// <summary>
    /// AD attribute that <c>AD.CreateUser</c> fills with the request id, so an account can be
    /// matched to its request even if its objectGUID was not stored (DECISIONS K6).
    /// </summary>
    public string RequestIdAttribute { get; set; } = "";

    /// <summary>Thumbprint of the worker certificate used to encrypt the initial password.</summary>
    public string PasswordCertThumbprint { get; set; } = "";

    public ExecutionConfig Execution { get; set; } = new();

    /// <summary>
    /// Who may approve a request created by an ITAdmin themself (DECISIONS A1). Missing in
    /// stored configurations → <see cref="ApprovalPolicy.SelfApprovalWithReason"/>.
    /// </summary>
    public ApprovalPolicy ApprovalPolicy { get; set; } = ApprovalPolicy.SelfApprovalWithReason;
}

/// <summary>Approval rule for requests created by the approving ITAdmin (DECISIONS A1).</summary>
public enum ApprovalPolicy
{
    /// <summary>Creator and approver must differ (SPEC §9).</summary>
    FourEyes,

    /// <summary>An ITAdmin may approve their own request with a mandatory, audited reason.</summary>
    SelfApprovalWithReason,
}

public sealed class DoctorTitleConfig
{
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class HomeConfig
{
    public string Server { get; set; } = "";
    public string LocalRoot { get; set; } = "";
    public string ShareNamePattern { get; set; } = "";
    public string UncPattern { get; set; } = "";

    /// <summary>NTFS right of the user on the own home folder (DECISIONS X17).</summary>
    public HomeRight UserRight { get; set; } = HomeRight.Modify;

    /// <summary>Additional ACEs on every home folder; must also be in the endpoint allowlist on the file server.</summary>
    public List<HomeAce> AdditionalAces { get; set; } = [];
}

public sealed class LogonScriptLocationConfig
{
    /// <summary>Domain controller with the JEA endpoint for logon scripts (separate from Home.Server).</summary>
    public string Server { get; set; } = "";

    public string Path { get; set; } = "";
    public string FileNamePattern { get; set; } = "";
}

public sealed class TeamsGlobalConfig
{
    public string VoiceRoutingPolicy { get; set; } = "";
    public string VoicemailPolicy { get; set; } = "";
    public string VoicemailPromptLanguage { get; set; } = "";
    public string PhoneNumberType { get; set; } = "";
}

public enum LicenseMode
{
    Direct,
    Group,
}

/// <summary>Worker execution settings (SPEC §6).</summary>
public sealed class ExecutionConfig
{
    public TimeSpan PollInterval { get; set; }

    /// <summary>Delays between attempts of a waiting step; the last value repeats.</summary>
    public List<TimeSpan> BackoffSchedule { get; set; } = [];

    /// <summary>Default timeout per step, measured from its first attempt.</summary>
    public TimeSpan StepTimeout { get; set; }
}
