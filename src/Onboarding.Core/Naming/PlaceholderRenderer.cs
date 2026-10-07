using System.Collections.Frozen;
using System.Text;

namespace Onboarding.Core.Naming;

/// <summary>Known template placeholders.</summary>
public static class Placeholders
{
    public const string FirstInitial = "firstInitial";
    public const string FirstName = "firstName";
    public const string LastName = "lastName";
    public const string MailLocal = "mailLocal";
    public const string Sam = "sam";
    public const string Extension = "DW";
    public const string HomeUnc = "homeUnc";
    public const string Department = "department";

    public static FrozenSet<string> All { get; } =
        new[] { FirstInitial, FirstName, LastName, MailLocal, Sam, Extension, HomeUnc, Department }
            .ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// Renders <c>{name}</c> placeholders. Unknown placeholders are configuration errors and throw;
/// known placeholders without a value make rendering fail softly (the caller decides).
/// </summary>
public static class PlaceholderRenderer
{
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        if (!TryRender(template, values, out var result, out var missing))
        {
            throw new TemplateException($"Template '{template}' needs a value for {{{missing}}}.");
        }

        return result;
    }

    public static bool TryRender(
        string template,
        IReadOnlyDictionary<string, string> values,
        out string result,
        out string? missingPlaceholder)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        var sb = new StringBuilder(template.Length + 16);
        missingPlaceholder = null;
        var i = 0;
        while (i < template.Length)
        {
            var ch = template[i];
            if (ch == '}')
            {
                throw new TemplateException($"Unmatched '}}' in template '{template}'.");
            }

            if (ch != '{')
            {
                sb.Append(ch);
                i++;
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                throw new TemplateException($"Unclosed '{{' in template '{template}'.");
            }

            var name = template[(i + 1)..end];
            if (!Placeholders.All.Contains(name))
            {
                throw new TemplateException($"Unknown placeholder {{{name}}} in template '{template}'.");
            }

            if (!values.TryGetValue(name, out var value))
            {
                missingPlaceholder ??= name;
            }
            else
            {
                sb.Append(value);
            }

            i = end + 1;
        }

        result = sb.ToString();
        return missingPlaceholder is null;
    }

    /// <summary>Validates template syntax and placeholder names without values.</summary>
    public static void Validate(string template)
    {
        var all = Placeholders.All.ToDictionary(p => p, _ => "");
        _ = Render(template, all);
    }
}

/// <summary>Invalid template in the configuration.</summary>
public sealed class TemplateException : Exception
{
    public TemplateException()
    {
    }

    public TemplateException(string message)
        : base(message)
    {
    }

    public TemplateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
