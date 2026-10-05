using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;

namespace Onboarding.Tests.TestSupport;

/// <summary>Test configuration mirroring the SPEC §4 examples. Built here, not taken from seed data.</summary>
internal static class TestConfig
{
    public static readonly Guid AreaId = new("00000000-0000-0000-0000-00000000a001");
    public static readonly Guid OtherAreaId = new("00000000-0000-0000-0000-00000000a002");
    public static readonly Guid DepartmentId = new("00000000-0000-0000-0000-00000000d001");
    public static readonly Guid OtherDepartmentId = new("00000000-0000-0000-0000-00000000d002");

    public static GlobalConfig Global() => new()
    {
        DomainFqdn = "CC.local",
        UpnSuffix = "cleancontrolling.de",
        MailPattern = "{firstInitial}.{lastName}@cleancontrolling.de",
        ProxyAddressTemplates = ["SMTP:{mailLocal}@cleancontrolling.de", "smtp:{mailLocal}@cleancontrolling.com"],
        DoctorTitle = new DoctorTitleConfig { Attribute = "extensionAttribute1", Value = "Dr." },
        PhonePrefixE164 = "+497465929678",
        PhoneDisplayFormat = "+49 7465 929678-{DW}",
        Home = new HomeConfig
        {
            Server = "DC01",
            LocalRoot = @"F:\Home",
            ShareNamePattern = "{sam}$",
            UncPattern = @"\\dc01\{sam}$",
        },
        LogonScript = new LogonScriptLocationConfig { Path = @"\\dc03\NETLOGON", FileNamePattern = "{sam}.bat" },
        UsageLocation = "DE",
        LicenseMode = LicenseMode.Direct,
        TimeZone = "Europe/Berlin",
        EnableLeadTime = TimeSpan.Zero,
        Execution = new ExecutionConfig
        {
            PollInterval = TimeSpan.FromSeconds(60),
            BackoffSchedule = [Minutes(1), Minutes(2), Minutes(5), Minutes(10), Minutes(15)],
            StepTimeout = TimeSpan.FromHours(24),
        },
    };

    private static TimeSpan Minutes(int m) => TimeSpan.FromMinutes(m);

    public static AreaConfig Area() => new()
    {
        Id = AreaId,
        Name = "TecSa",
        OuDistinguishedName = "OU=Users,OU=Technical,OU=CleanControlling,DC=CC,DC=local",
        OuCanonical = "CC.local/CleanControlling/Technical/Users",
        Company = "CleanControlling GmbH",
    };

    public static DepartmentConfig Department() => new()
    {
        Id = DepartmentId,
        Name = "Vertrieb",
        AdGroups = ["GG-Vertrieb"],
        Licenses = new LicenseConfig { SkuPartNumbers = ["SPB"], SkuPartNumbersIfPhone = ["MCOEV"] },
        SharedMailboxes =
        [
            new SharedMailboxConfig { Mailbox = "vertrieb@cleancontrolling.de", FullAccess = true, AutoMapping = true, SendAs = true },
        ],
        LogonScript = new LogonScriptTemplate
        {
            DisconnectDrives = ["S", "H", "U", "T", "V"],
            ConnectDrives =
            [
                new DriveMapping { Letter = "H", Unc = "{homeUnc}" },
                new DriveMapping { Letter = "S", Unc = @"\\dc01\Vertrieb" },
            ],
        },
        Teams = new DepartmentTeamsConfig
        {
            PhoneExpected = true,
            Voicemail = true,
            UnansweredForward = new UnansweredForwardConfig
            {
                Enabled = true,
                Delay = TimeSpan.FromSeconds(20),
                TargetType = "singleTarget",
                Target = "CCVertrieb@cleancontrolling.de",
            },
        },
    };

    public static RequestConfigSnapshot Snapshot(
        Action<GlobalConfig>? global = null,
        Action<DepartmentConfig>? department = null)
    {
        var g = Global();
        global?.Invoke(g);
        var d = Department();
        department?.Invoke(d);
        return new RequestConfigSnapshot { Global = g, Area = Area(), Department = d, TakenAt = DateTimeOffset.UnixEpoch };
    }

    public static PersonInput Person(
        string firstName = "Lenox",
        string lastName = "Irion",
        string mail = "",
        string? extension = null,
        bool doctor = false) => new()
        {
            FirstName = firstName,
            LastName = lastName,
            Mail = mail,
            HasDoctorTitle = doctor,
            AreaId = AreaId,
            DepartmentId = DepartmentId,
            ManagerObjectGuid = new Guid("11111111-2222-3333-4444-555555555555"),
            EffectiveDate = new DateOnly(2026, 11, 2),
            Extension = extension,
        };
}
