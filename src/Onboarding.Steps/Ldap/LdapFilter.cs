using System.Globalization;
using System.Text;

namespace Onboarding.Steps.Ldap;

/// <summary>
/// RFC 4515 escaping for values in LDAP search filters (DECISIONS X9). Every value that comes
/// from user input or configuration goes through <see cref="Escape"/>; filters are never built
/// from unescaped strings.
/// </summary>
public static class LdapFilter
{
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            _ = c switch
            {
                '\\' => builder.Append(@"\5c"),
                '*' => builder.Append(@"\2a"),
                '(' => builder.Append(@"\28"),
                ')' => builder.Append(@"\29"),
                '\0' => builder.Append(@"\00"),
                _ => builder.Append(c),
            };
        }

        return builder.ToString();
    }

    /// <summary>Escapes binary values byte by byte, e.g. an objectGUID.</summary>
    public static string EscapeBytes(ReadOnlySpan<byte> value)
    {
        var builder = new StringBuilder(value.Length * 3);
        foreach (var b in value)
        {
            builder.Append('\\').Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// objectGUID filter value. AD stores the GUID in the byte order of
    /// <see cref="Guid.ToByteArray()"/>.
    /// </summary>
    public static string EscapeGuid(Guid value) => EscapeBytes(value.ToByteArray());

    /// <summary>
    /// Validates an attribute name taken from configuration (e.g. the request-id attribute)
    /// before it is used in a filter or attribute list.
    /// </summary>
    public static string AttributeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length > 0 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? name
            : throw new InvalidOperationException($"Invalid LDAP attribute name '{name}'.");
    }

    /// <summary>"cc.local" → "DC=cc,DC=local".</summary>
    public static string DomainToBaseDn(string domainFqdn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainFqdn);
        var labels = domainFqdn.Trim().TrimEnd('.').Split('.');
        if (labels.Any(l => l.Length == 0 || !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new InvalidOperationException($"Invalid domain name '{domainFqdn}'.");
        }

        return string.Join(',', labels.Select(l => "DC=" + l));
    }
}
