using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Core.Naming;
using Onboarding.Tests.TestSupport;

namespace Onboarding.Tests.Core;

public sealed class IdentityDeriverTests
{
    private static DerivationResult Derive(PersonInput input, IdentityOverride? identityOverride = null, GlobalConfig? global = null) =>
        IdentityDeriver.Derive(input, identityOverride, global ?? TestConfig.Global(), TestConfig.Area(), TestConfig.Department());

    private static DerivedIdentity DeriveOk(PersonInput input, IdentityOverride? identityOverride = null)
    {
        var result = Derive(input, identityOverride);
        Assert.False(result.NeedsInput, string.Join(" | ", result.Issues.Select(i => i.Message)));
        return result.Identity!;
    }

    [Fact]
    public void Spec_example_Lenox_Irion()
    {
        var id = DeriveOk(TestConfig.Person());

        Assert.Equal("lirion", id.SamAccountName);
        Assert.Equal("l.irion@cleancontrolling.de", id.Mail);
        Assert.Equal("l.irion@cleancontrolling.de", id.UserPrincipalName);
        Assert.Equal(["SMTP:l.irion@cleancontrolling.de", "smtp:l.irion@cleancontrolling.com"], id.ProxyAddresses);
        Assert.Equal("Lenox", id.GivenName);
        Assert.Equal("Irion", id.Surname);
        Assert.Equal("Lenox Irion", id.DisplayName);
        Assert.Equal("Vertrieb", id.Department);
        Assert.Equal("CleanControlling GmbH", id.Company);
        Assert.Equal("OU=Users,OU=Technical,OU=CleanControlling,DC=CC,DC=local", id.OuDistinguishedName);
        Assert.Equal("lirion.bat", id.ScriptPath);
        Assert.Equal(@"\\dc01\lirion$", id.HomeUnc);
        Assert.Empty(id.AdditionalAttributes);
    }

    [Theory]
    [InlineData("Jürgen", "Größmann", "jgroessmann", "j.groessmann")]
    [InlineData("Özlem", "Yılmaz", null, null)] // dotless i → NeedsInput
    [InlineData("Özlem", "Schäfer", "oschaefer", "o.schaefer")] // initial after transliteration: Ö → oe → o
    [InlineData("ÄNNE", "ÜBEL", "auebel", "a.uebel")]
    [InlineData("José", "Núñez", "jnunez", "j.nunez")]
    [InlineData("Søren", "Kierkegaard", "skierkegaard", "s.kierkegaard")]
    [InlineData("Łukasz", "Łabędź", "llabedz", "l.labedz")]
    [InlineData("Œlif", "Æbelø", "oaebelo", "o.aebelo")]
    [InlineData("Đorđe", "Weiß", "dweiss", "d.weiss")]
    [InlineData("Sean", "O'Brien", "sobrien", "s.obrien")]
    public void Transliterates_names(string first, string last, string? sam, string? mailLocal)
    {
        var result = Derive(TestConfig.Person(first, last));
        if (sam is null)
        {
            Assert.True(result.NeedsInput);
            Assert.Contains(result.Issues, i => i.Code == IdentityIssueCode.NotTransliterable);
            return;
        }

        Assert.False(result.NeedsInput);
        Assert.Equal(sam, result.Identity!.SamAccountName);
        Assert.Equal($"{mailLocal}@cleancontrolling.de", result.Identity.Mail);
    }

    [Theory]
    [InlineData("Anna-Lena", "Schmidt")]
    [InlineData("Max", "Müller-Lüdenscheidt")]
    [InlineData("Ursula", "von der Leyen")]
    [InlineData("Anna Lena", "Schmidt")]
    [InlineData("Max", "Müller–Lüdenscheidt")] // en dash
    [InlineData("Max", "Müller\u00A0Lüdenscheidt")] // no-break space
    public void Double_names_need_input(string first, string last)
    {
        var result = Derive(TestConfig.Person(first, last));

        Assert.True(result.NeedsInput);
        Assert.Null(result.Identity);
        Assert.Contains(result.Issues, i => i.Code == IdentityIssueCode.DoubleName);
    }

    [Fact]
    public void Double_name_needs_input_even_if_requester_typed_a_mail()
    {
        var result = Derive(TestConfig.Person("Max", "Müller-Lüdenscheidt", mail: "m.mueller-luedenscheidt@cleancontrolling.de"));

        Assert.True(result.NeedsInput);
        Assert.Contains(result.Issues, i => i.Code == IdentityIssueCode.DoubleName);
    }

    [Fact]
    public void Double_name_with_admin_override_derives_and_proxies_follow_mail()
    {
        var id = DeriveOk(
            TestConfig.Person("Max", "Müller-Lüdenscheidt"),
            new IdentityOverride("mmueller", "m.mueller-luedenscheidt@cleancontrolling.de"));

        Assert.Equal("mmueller", id.SamAccountName);
        Assert.Equal("m.mueller-luedenscheidt@cleancontrolling.de", id.Mail);
        Assert.Equal("m.mueller-luedenscheidt@cleancontrolling.de", id.UserPrincipalName);
        Assert.Equal(
            ["SMTP:m.mueller-luedenscheidt@cleancontrolling.de", "smtp:m.mueller-luedenscheidt@cleancontrolling.com"],
            id.ProxyAddresses);
        Assert.Equal("Max Müller-Lüdenscheidt", id.DisplayName);
        Assert.Equal("mmueller.bat", id.ScriptPath);
    }

    [Theory]
    [InlineData("m.mueller", "x@cleancontrolling.de", IdentityIssueCode.InvalidSam)] // dot in sam
    [InlineData("mmüller", "x@cleancontrolling.de", IdentityIssueCode.InvalidSam)]
    [InlineData("abcdefghijklmnopqrstu", "x@cleancontrolling.de", IdentityIssueCode.InvalidSam)] // 21
    [InlineData("mmueller", "-m.mueller@cleancontrolling.de", IdentityIssueCode.InvalidMail)]
    [InlineData("mmueller", "m..mueller@cleancontrolling.de", IdentityIssueCode.InvalidMail)]
    [InlineData("mmueller", "m.mueller", IdentityIssueCode.InvalidMail)]
    public void Override_values_are_validated(string sam, string mail, IdentityIssueCode expected)
    {
        var result = Derive(TestConfig.Person("Max", "Müller-Lüdenscheidt"), new IdentityOverride(sam, mail));

        Assert.True(result.NeedsInput);
        Assert.Contains(result.Issues, i => i.Code == expected);
    }

    [Fact]
    public void Sam_with_exactly_20_characters_is_accepted()
    {
        // l + 19 characters = 20
        var id = DeriveOk(TestConfig.Person("Lena", "Abcdefghijklmnopqrs"));
        Assert.Equal(20, id.SamAccountName.Length);
        Assert.Equal("labcdefghijklmnopqrs", id.SamAccountName);
    }

    [Fact]
    public void Sam_with_21_characters_needs_input_and_is_not_truncated()
    {
        var result = Derive(TestConfig.Person("Lena", "Abcdefghijklmnopqrst"));

        Assert.True(result.NeedsInput);
        Assert.Null(result.Identity);
        Assert.Contains(result.Issues, i => i.Code == IdentityIssueCode.SamTooLong);
    }

    [Fact]
    public void Umlaut_expansion_beyond_20_characters_needs_input()
    {
        // u + 19 letters = 20 without expansion, but ä/ä/ü transliterate to 23 characters.
        var result = Derive(TestConfig.Person("Ute", "Schwärzenbächlermül"));

        Assert.True(result.NeedsInput);
        Assert.Contains(result.Issues, i => i.Code == IdentityIssueCode.SamTooLong);
    }

    [Fact]
    public void Edited_mail_is_used_and_proxies_follow_it()
    {
        var id = DeriveOk(TestConfig.Person(mail: "Lenox.Irion@cleancontrolling.de"));

        Assert.Equal("lirion", id.SamAccountName);
        Assert.Equal("lenox.irion@cleancontrolling.de", id.Mail);
        Assert.Equal("lenox.irion@cleancontrolling.de", id.UserPrincipalName);
        Assert.Equal(["SMTP:lenox.irion@cleancontrolling.de", "smtp:lenox.irion@cleancontrolling.com"], id.ProxyAddresses);
    }

    [Fact]
    public void Proxy_prefix_case_is_kept_verbatim()
    {
        var id = DeriveOk(TestConfig.Person());

        Assert.StartsWith("SMTP:", id.ProxyAddresses[0], StringComparison.Ordinal);
        Assert.StartsWith("smtp:", id.ProxyAddresses[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Templates_without_matching_primary_need_input()
    {
        var global = TestConfig.Global();
        global.ProxyAddressTemplates = ["smtp:{mailLocal}@cleancontrolling.com"];

        var result = Derive(TestConfig.Person(), global: global);

        Assert.Contains(result.Issues, i => i.Code == IdentityIssueCode.PrimaryProxyMismatch);
    }

    [Fact]
    public void Template_using_name_placeholder_fails_softly_for_override()
    {
        var global = TestConfig.Global();
        global.ProxyAddressTemplates = ["SMTP:{mailLocal}@cleancontrolling.de", "smtp:{firstInitial}.{lastName}@cleancontrolling.com"];

        var result = Derive(TestConfig.Person("Max", "Müller-Lüdenscheidt"), new IdentityOverride("mmueller", "m.mueller@cleancontrolling.de"), global);

        Assert.Contains(result.Issues, i => i.Code == IdentityIssueCode.TemplateValueMissing);
    }

    [Fact]
    public void Unknown_placeholder_is_a_configuration_error()
    {
        var global = TestConfig.Global();
        global.MailPattern = "{vorname}@cleancontrolling.de";

        Assert.Throws<TemplateException>(() => Derive(TestConfig.Person(), global: global));
    }

    [Fact]
    public void Phone_from_extension()
    {
        var id = DeriveOk(TestConfig.Person(extension: "0"));

        Assert.Equal("+4974659296780", id.PhoneE164);
        Assert.Equal("0", id.Extension);
        Assert.Equal("+49 7465 929678-0", id.TelephoneNumber);
    }

    [Fact]
    public void Phone_with_four_digit_extension()
    {
        var id = DeriveOk(TestConfig.Person(extension: "1234"));

        Assert.Equal("+4974659296781234", id.PhoneE164);
        Assert.Equal("+49 7465 929678-1234", id.TelephoneNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_extension_leaves_phone_empty(string? extension)
    {
        var id = DeriveOk(TestConfig.Person(extension: extension));

        Assert.Null(id.PhoneE164);
        Assert.Null(id.Extension);
        Assert.Null(id.TelephoneNumber);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12a")]
    [InlineData("+49")]
    [InlineData("١٢")] // Arabic-Indic digits
    public void Invalid_extension_needs_input(string extension)
    {
        var result = Derive(TestConfig.Person(extension: extension));

        Assert.Contains(result.Issues, i => i.Code == IdentityIssueCode.InvalidExtension);
    }

    [Fact]
    public void Doctor_title_sets_configured_attribute()
    {
        var id = DeriveOk(TestConfig.Person(doctor: true));

        Assert.Equal("Dr.", id.AdditionalAttributes["extensionAttribute1"]);
        Assert.Equal("Lenox Irion", id.DisplayName);
    }

    [Fact]
    public void Missing_name_needs_input()
    {
        var result = Derive(TestConfig.Person("", "Irion"));

        Assert.Contains(result.Issues, i => i.Code == IdentityIssueCode.MissingName);
    }

    [Theory]
    [InlineData("Lenox", "Irion", "l.irion@cleancontrolling.de")]
    [InlineData("Özlem", "Schäfer", "o.schaefer@cleancontrolling.de")]
    [InlineData("Anna-Lena", "Schmidt", null)]
    public void Suggest_mail_for_form(string first, string last, string? expected)
    {
        Assert.Equal(expected, IdentityDeriver.SuggestMail(first, last, TestConfig.Global()));
    }
}

public sealed class InputWarningsTests
{
    [Theory]
    [InlineData(true, "12", false)]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(false, "12", true)]
    public void Phone_warning(bool phoneExpected, string? extension, bool warns)
    {
        var department = TestConfig.Department();
        department.Teams.PhoneExpected = phoneExpected;

        Assert.Equal(warns, InputWarnings.Phone(department, extension) is not null);
    }
}
