namespace Onboarding.Core.Naming;

/// <summary>Validates sAMAccountName, mail address and extension (DECISIONS D6).</summary>
public static class IdentityValidator
{
    public const int MaxSamLength = 20;
    public const int MaxExtensionLength = 4;

    /// <summary><c>[a-z0-9]</c>, 1–20 characters, no dots. Returns an error text or null.</summary>
    public static string? ValidateSam(string? sam)
    {
        if (string.IsNullOrEmpty(sam))
        {
            return "sAMAccountName fehlt.";
        }

        if (sam.Length > MaxSamLength)
        {
            return $"sAMAccountName ist länger als {MaxSamLength} Zeichen.";
        }

        return sam.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            ? null
            : "sAMAccountName darf nur a-z und 0-9 enthalten.";
    }

    /// <summary>
    /// Mail local part: <c>[a-z0-9.-]</c>, no dot or hyphen at start or end, no "..".
    /// Returns an error text or null.
    /// </summary>
    public static string? ValidateMailLocalPart(string? localPart)
    {
        if (string.IsNullOrEmpty(localPart))
        {
            return "Lokaler Teil der E-Mail-Adresse fehlt.";
        }

        if (!localPart.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
        {
            return "E-Mail-Adresse (lokaler Teil) darf nur a-z, 0-9, '.' und '-' enthalten.";
        }

        if (localPart[0] is '.' or '-' || localPart[^1] is '.' or '-')
        {
            return "E-Mail-Adresse (lokaler Teil) darf nicht mit '.' oder '-' beginnen oder enden.";
        }

        return localPart.Contains("..", StringComparison.Ordinal)
            ? "E-Mail-Adresse (lokaler Teil) darf keine aufeinanderfolgenden Punkte enthalten."
            : null;
    }

    /// <summary>Validates a full address (local part rules plus a plausible lower-case domain).</summary>
    public static string? ValidateMail(string? mail)
    {
        if (string.IsNullOrWhiteSpace(mail))
        {
            return "E-Mail-Adresse fehlt.";
        }

        var parts = mail.Split('@');
        if (parts.Length != 2)
        {
            return "E-Mail-Adresse muss genau ein '@' enthalten.";
        }

        var localError = ValidateMailLocalPart(parts[0]);
        if (localError is not null)
        {
            return localError;
        }

        var domain = parts[1];
        var labels = domain.Split('.');
        var domainValid = labels.Length >= 2 && labels.All(l =>
            l.Length > 0 && l[0] != '-' && l[^1] != '-' &&
            l.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'));
        return domainValid ? null : "Domain der E-Mail-Adresse ist ungültig.";
    }

    /// <summary>Extension: digits only, 1–4 digits. Null/empty means "no extension" and is valid.</summary>
    public static string? ValidateExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        return extension.Length <= MaxExtensionLength && extension.All(char.IsAsciiDigit)
            ? null
            : $"Durchwahl: nur Ziffern, 1–{MaxExtensionLength} Stellen.";
    }

    public static string LocalPart(string mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        var at = mail.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? mail : mail[..at];
    }
}
