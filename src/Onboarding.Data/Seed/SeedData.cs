using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Data.Entities;

namespace Onboarding.Data.Seed;

/// <summary>
/// Initial configuration from SPEC §4.1, §4.2 and §4.5 (with DECISIONS D7, S5). This is data,
/// not logic: it is written once by the migration and maintained in the admin UI afterwards.
/// Values marked TBD in the spec are left empty.
/// </summary>
public static class SeedData
{
    public static readonly Guid AreaTecSa = new("6a4c0f6e-6f0b-4a51-9d0e-1a7a3f0c0001");
    public static readonly Guid AreaBio = new("6a4c0f6e-6f0b-4a51-9d0e-1a7a3f0c0002");
    public static readonly Guid AreaChemie = new("6a4c0f6e-6f0b-4a51-9d0e-1a7a3f0c0003");

    public static GlobalConfig Global() => new()
    {
        DomainFqdn = "CC.local",
        DomainNetBios = "", // SPEC §11: TBD
        UpnSuffix = "cleancontrolling.de",
        MailPattern = "{firstInitial}.{lastName}@cleancontrolling.de",
        ProxyAddressTemplates =
        [
            "SMTP:{mailLocal}@cleancontrolling.de",
            "smtp:{mailLocal}@cleancontrolling.com",
        ],
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
        LogonScript = new LogonScriptLocationConfig
        {
            Path = @"\\dc03\NETLOGON",
            FileNamePattern = "{sam}.bat",
        },
        EntraConnectServer = "CC01",
        UsageLocation = "DE",
        LicenseMode = LicenseMode.Direct,
        OneWinNativeOutlookEnabled = false,
        Teams = new TeamsGlobalConfig
        {
            VoiceRoutingPolicy = "INTStandard",
            VoicemailPolicy = "CleanControlling Personal Voicemail",
            VoicemailPromptLanguage = "de-DE",
            PhoneNumberType = "DirectRouting",
        },
        DisabledUsersOU = "", // SPEC §4.1: TBD (v2)
        TimeZone = "Europe/Berlin",
        EnableLeadTime = TimeSpan.Zero,
        PasswordCertThumbprint = "",
        Execution = new ExecutionConfig
        {
            PollInterval = TimeSpan.FromSeconds(60),
            BackoffSchedule =
            [
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(15),
            ],
            StepTimeout = TimeSpan.FromHours(24),
        },
    };

    public static IReadOnlyList<AreaConfig> Areas() =>
    [
        new()
        {
            Id = AreaTecSa,
            Name = "TecSa",
            OuCanonical = "CC.local/CleanControlling/Technical/Users",
            OuDistinguishedName = "OU=Users,OU=Technical,OU=CleanControlling,DC=CC,DC=local",
            Company = "CleanControlling GmbH",
            SortOrder = 1,
        },
        new()
        {
            Id = AreaBio,
            Name = "Bio",
            OuCanonical = "CC.local/CleanControlling/Medical/Biology/Users",
            OuDistinguishedName = "OU=Users,OU=Biology,OU=Medical,OU=CleanControlling,DC=CC,DC=local",
            Company = "CleanControlling Medical GmbH & Co. KG",
            SortOrder = 2,
        },
        new()
        {
            Id = AreaChemie,
            Name = "Chemie",
            OuCanonical = "CC.local/CleanControlling/Medical/Chemical/Users",
            OuDistinguishedName = "OU=Users,OU=Chemical,OU=Medical,OU=CleanControlling,DC=CC,DC=local",
            Company = "CleanControlling Medical GmbH & Co. KG",
            SortOrder = 3,
        },
    ];

    public static IReadOnlyList<ChecklistTemplate> OnboardingChecklist()
    {
        string[] titles =
        [
            "SwissSign-Zertifikat (S/MIME) erstellen",
            "ILIAS-Konto anlegen",
            "Mail an HR",
            "Einladung EDV-Einführung",
            "MFA einrichten (1Password)",
            "In Multifunktionsdrucker eintragen",
            "Rechnerarbeitsplatz einrichten",
        ];

        return titles.Select((title, i) => new ChecklistTemplate
        {
            Id = new Guid($"8d1e2b3c-4f5a-4b6c-8d7e-0000000001{i + 1:00}"),
            Type = RequestType.Onboarding,
            Scope = ChecklistScope.Global,
            Title = title,
            Mandatory = true,
            SortOrder = (i + 1) * 10,
        }).ToList();
    }

    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GlobalConfigRecord>().HasData(new GlobalConfigRecord
        {
            Id = GlobalConfigRecord.SingletonId,
            Settings = Global(),
        });
        modelBuilder.Entity<AreaConfig>().HasData(Areas());
        modelBuilder.Entity<ChecklistTemplate>().HasData(OnboardingChecklist());
    }
}
