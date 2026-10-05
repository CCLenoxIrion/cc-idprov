using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;

namespace Onboarding.Core.Naming;

/// <summary>
/// Derives sAMAccountName, mail, UPN, proxyAddresses, company/OU, phone etc. (SPEC §2).
/// Never numbers or truncates: anything that cannot be derived cleanly yields issues
/// (→ <c>NeedsInput</c>). Invalid templates throw <see cref="TemplateException"/>.
/// </summary>
public static class IdentityDeriver
{
    private const string PrimarySmtpPrefix = "SMTP:";

    /// <summary>Mail suggestion for pre-filling the request form; null if not derivable.</summary>
    public static string? SuggestMail(string firstName, string lastName, GlobalConfig global)
    {
        ArgumentNullException.ThrowIfNull(global);
        var parts = TryNameParts(firstName, lastName, out _);
        return parts is null
            ? null
            : PlaceholderRenderer.Render(global.MailPattern, parts.Value.ToValues());
    }

    public static DerivationResult Derive(
        PersonInput input,
        IdentityOverride? identityOverride,
        GlobalConfig global,
        AreaConfig area,
        DepartmentConfig department)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(department);

        var issues = new List<IdentityIssue>();
        var firstName = input.FirstName.Trim();
        var lastName = input.LastName.Trim();
        if (firstName.Length == 0 || lastName.Length == 0)
        {
            issues.Add(new(IdentityIssueCode.MissingName, "Vor- und Nachname sind Pflichtfelder."));
            return new DerivationResult(null, issues);
        }

        var parts = TryNameParts(firstName, lastName, out var nameIssue);
        string? sam;
        string? mail;
        if (identityOverride is not null)
        {
            sam = identityOverride.SamAccountName.Trim();
            mail = identityOverride.Mail.Trim().ToLowerInvariant();
        }
        else
        {
            if (nameIssue is not null)
            {
                issues.Add(nameIssue);
            }

            sam = parts is null ? null : parts.Value.Initial + parts.Value.Last;
            if (sam is not null && sam.Length > IdentityValidator.MaxSamLength)
            {
                issues.Add(new(IdentityIssueCode.SamTooLong,
                    $"Abgeleiteter sAMAccountName '{sam}' ist länger als {IdentityValidator.MaxSamLength} Zeichen; bitte manuell vergeben."));
                sam = null;
            }

            mail = string.IsNullOrWhiteSpace(input.Mail)
                ? parts is null ? null : PlaceholderRenderer.Render(global.MailPattern, parts.Value.ToValues())
                : input.Mail.Trim().ToLowerInvariant();
        }

        if (sam is not null && IdentityValidator.ValidateSam(sam) is { } samError)
        {
            issues.Add(new(IdentityIssueCode.InvalidSam, samError));
        }

        if (mail is not null && IdentityValidator.ValidateMail(mail) is { } mailError)
        {
            issues.Add(new(IdentityIssueCode.InvalidMail, mailError));
        }

        var extension = string.IsNullOrWhiteSpace(input.Extension) ? null : input.Extension.Trim();
        if (IdentityValidator.ValidateExtension(extension) is { } extensionError)
        {
            issues.Add(new(IdentityIssueCode.InvalidExtension, extensionError));
        }

        if (issues.Count > 0 || sam is null || mail is null)
        {
            return new DerivationResult(null, issues);
        }

        var values = parts?.ToValues() ?? new Dictionary<string, string>(StringComparer.Ordinal);
        values[Placeholders.MailLocal] = IdentityValidator.LocalPart(mail);
        values[Placeholders.Sam] = sam;
        if (extension is not null)
        {
            values[Placeholders.Extension] = extension;
        }

        var proxyAddresses = new List<string>();
        foreach (var template in global.ProxyAddressTemplates)
        {
            if (PlaceholderRenderer.TryRender(template, values, out var proxy, out var missing))
            {
                proxyAddresses.Add(proxy);
            }
            else
            {
                issues.Add(new(IdentityIssueCode.TemplateValueMissing,
                    $"proxyAddresses-Vorlage '{template}' benötigt {{{missing}}}, das für diesen Auftrag nicht ableitbar ist."));
            }
        }

        var primaries = proxyAddresses.Where(p => p.StartsWith(PrimarySmtpPrefix, StringComparison.Ordinal)).ToList();
        if (issues.Count == 0 &&
            (primaries.Count != 1 ||
             !string.Equals(primaries[0][PrimarySmtpPrefix.Length..], mail, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new(IdentityIssueCode.PrimaryProxyMismatch,
                "proxyAddresses-Vorlagen müssen genau eine primäre Adresse (SMTP:) ergeben, die der E-Mail-Adresse entspricht."));
        }

        if (issues.Count > 0)
        {
            return new DerivationResult(null, issues);
        }

        var homeUnc = PlaceholderRenderer.Render(global.Home.UncPattern, values);
        values[Placeholders.HomeUnc] = homeUnc;

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input.HasDoctorTitle)
        {
            attributes[global.DoctorTitle.Attribute] = global.DoctorTitle.Value;
        }

        var identity = new DerivedIdentity
        {
            SamAccountName = sam,
            Mail = mail,
            UserPrincipalName = mail,
            ProxyAddresses = proxyAddresses,
            GivenName = firstName,
            Surname = lastName,
            DisplayName = $"{firstName} {lastName}",
            Department = department.Name,
            Company = area.Company,
            OuDistinguishedName = area.OuDistinguishedName,
            ScriptPath = PlaceholderRenderer.Render(global.LogonScript.FileNamePattern, values),
            HomeUnc = homeUnc,
            Extension = extension,
            PhoneE164 = extension is null ? null : global.PhonePrefixE164 + extension,
            TelephoneNumber = extension is null ? null : PlaceholderRenderer.Render(global.PhoneDisplayFormat, values),
            AdditionalAttributes = attributes,
        };
        return new DerivationResult(identity, []);
    }

    private static NameParts? TryNameParts(string firstName, string lastName, out IdentityIssue? issue)
    {
        issue = null;
        firstName = firstName.Trim();
        lastName = lastName.Trim();
        if (IsDoubleName(firstName) || IsDoubleName(lastName))
        {
            issue = new(IdentityIssueCode.DoubleName,
                "Doppelname (Bindestrich oder Leerzeichen): sAMAccountName und E-Mail bitte manuell vergeben.");
            return null;
        }

        var first = Transliterator.TryTransliterate(firstName);
        var last = Transliterator.TryTransliterate(lastName);
        if (first is null || last is null)
        {
            issue = new(IdentityIssueCode.NotTransliterable,
                "Name enthält Zeichen, die nicht automatisch umgeschrieben werden können; bitte manuell vergeben.");
            return null;
        }

        return new NameParts(first[..1], first, last);
    }

    private static bool IsDoubleName(string name) => name.Any(c => c == '-' || char.IsWhiteSpace(c) || IsDash(c));

    private static bool IsDash(char c) => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) ==
                                          System.Globalization.UnicodeCategory.DashPunctuation;

    private readonly record struct NameParts(string Initial, string First, string Last)
    {
        public Dictionary<string, string> ToValues() => new(StringComparer.Ordinal)
        {
            [Placeholders.FirstInitial] = Initial,
            [Placeholders.FirstName] = First,
            [Placeholders.LastName] = Last,
        };
    }
}
