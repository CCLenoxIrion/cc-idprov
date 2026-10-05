using Onboarding.Core.LogonScripts;
using Onboarding.Core.Naming;
using Onboarding.Core.Time;

namespace Onboarding.Core.Configuration;

/// <summary>Checks configuration before it is saved. Returns German error texts.</summary>
public static class ConfigValidator
{
    public static IReadOnlyList<string> ValidateGlobal(GlobalConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();

        Required(errors, config.DomainFqdn, "Domäne (FQDN)");
        Required(errors, config.UpnSuffix, "UPN-Suffix");
        Template(errors, config.MailPattern, "Mail-Muster", required: true);
        if (config.ProxyAddressTemplates.Count == 0)
        {
            errors.Add("Mindestens eine proxyAddresses-Vorlage ist erforderlich.");
        }

        foreach (var template in config.ProxyAddressTemplates)
        {
            Template(errors, template, $"proxyAddresses-Vorlage '{template}'", required: true);
        }

        if (config.ProxyAddressTemplates.Count(t => t.StartsWith("SMTP:", StringComparison.Ordinal)) != 1)
        {
            errors.Add("Genau eine proxyAddresses-Vorlage muss primär sein (Präfix 'SMTP:' in Großbuchstaben).");
        }

        Required(errors, config.DoctorTitle.Attribute, "Attribut für Doktortitel");
        Required(errors, config.DoctorTitle.Value, "Wert für Doktortitel");
        if (!config.PhonePrefixE164.StartsWith('+') || !config.PhonePrefixE164[1..].All(char.IsAsciiDigit) || config.PhonePrefixE164.Length < 4)
        {
            errors.Add("Telefon-Präfix muss im E.164-Format sein (z. B. +49…).");
        }

        Template(errors, config.PhoneDisplayFormat, "Anzeigeformat Telefon", required: true);
        Required(errors, config.Home.Server, "Home-Server");
        Required(errors, config.Home.LocalRoot, "Home-Stammverzeichnis");
        Template(errors, config.Home.ShareNamePattern, "Freigabename-Muster", required: true);
        Template(errors, config.Home.UncPattern, "UNC-Muster Home", required: true);
        Required(errors, config.LogonScript.Path, "Pfad Anmeldeskripte");
        Template(errors, config.LogonScript.FileNamePattern, "Dateiname-Muster Anmeldeskript", required: true);
        Required(errors, config.EntraConnectServer, "Entra-Connect-Server");
        if (config.UsageLocation.Length != 2)
        {
            errors.Add("UsageLocation muss ein zweistelliger Ländercode sein.");
        }

        Required(errors, config.RequestIdAttribute, "Attribut für Request-Id");
        try
        {
            BusinessCalendar.Resolve(config.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or InvalidOperationException)
        {
            errors.Add($"Zeitzone '{config.TimeZone}' ist unbekannt.");
        }

        if (config.EnableLeadTime < TimeSpan.Zero || config.EnableLeadTime > TimeSpan.FromDays(7))
        {
            errors.Add("Vorlauf für Kontoaktivierung muss zwischen 0 und 7 Tagen liegen.");
        }

        if (config.Execution.PollInterval < TimeSpan.FromSeconds(5))
        {
            errors.Add("Polling-Intervall muss mindestens 5 Sekunden betragen.");
        }

        if (config.Execution.BackoffSchedule.Count == 0 || config.Execution.BackoffSchedule.Any(d => d <= TimeSpan.Zero))
        {
            errors.Add("Backoff-Plan braucht mindestens eine positive Wartezeit.");
        }

        if (config.Execution.StepTimeout <= TimeSpan.Zero)
        {
            errors.Add("Step-Timeout muss positiv sein.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateArea(AreaConfig area)
    {
        ArgumentNullException.ThrowIfNull(area);
        var errors = new List<string>();
        Required(errors, area.Name, "Name");
        Required(errors, area.OuDistinguishedName, "OU");
        Required(errors, area.Company, "Firma (company)");
        return errors;
    }

    public static IReadOnlyList<string> ValidateDepartment(DepartmentConfig department)
    {
        ArgumentNullException.ThrowIfNull(department);
        var errors = new List<string>();
        Required(errors, department.Name, "Name");
        if (department.AdGroups.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("AD-Gruppen dürfen nicht leer sein.");
        }

        foreach (var mailbox in department.SharedMailboxes)
        {
            if (IdentityValidator.ValidateMail(mailbox.Mailbox.Trim().ToLowerInvariant()) is not null)
            {
                errors.Add($"Freigabepostfach '{mailbox.Mailbox}' ist keine gültige Adresse.");
            }
        }

        var forward = department.Teams.UnansweredForward;
        if (forward.Enabled && (string.IsNullOrWhiteSpace(forward.Target) || string.IsNullOrWhiteSpace(forward.TargetType)))
        {
            errors.Add("Rufweiterleitung: Ziel und Zieltyp sind erforderlich.");
        }

        try
        {
            // ASCII check uses the department name itself, as {department} would.
            LogonScriptGenerator.Generate(department.LogonScript, new LogonScriptValues("sample", @"\\server\sample$", department.Name));
        }
        catch (Exception ex) when (ex is LogonScriptException or TemplateException)
        {
            errors.Add($"Anmeldeskript: {ex.Message}");
        }

        return errors;
    }

    private static void Required(List<string> errors, string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label} ist erforderlich.");
        }
    }

    private static void Template(List<string> errors, string? template, string label, bool required)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            if (required)
            {
                errors.Add($"{label} ist erforderlich.");
            }

            return;
        }

        try
        {
            PlaceholderRenderer.Validate(template);
        }
        catch (TemplateException ex)
        {
            errors.Add($"{label}: {ex.Message}");
        }
    }
}
