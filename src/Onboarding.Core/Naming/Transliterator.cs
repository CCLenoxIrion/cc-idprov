using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Onboarding.Core.Naming;

/// <summary>
/// Transliterates a single name part to <c>[a-z]</c> (DECISIONS D2). Hyphens and whitespace are
/// not handled here: double names are rejected before (DECISIONS D4).
/// </summary>
public static class Transliterator
{
    // Applied after lower-casing. NFD does not decompose these characters, hence the explicit map.
    private static readonly FrozenDictionary<char, string> Map = new Dictionary<char, string>
    {
        ['ä'] = "ae",
        ['ö'] = "oe",
        ['ü'] = "ue",
        ['ß'] = "ss",
        ['ø'] = "o",
        ['æ'] = "ae",
        ['œ'] = "oe",
        ['ł'] = "l",
        ['đ'] = "d",
    }.ToFrozenDictionary();

    // Apostrophe variants that are dropped (O'Brien -> obrien).
    private static readonly FrozenSet<char> Dropped = new[] { '\'', '’', '‘', 'ʼ', '`', '´' }.ToFrozenSet();

    /// <summary>
    /// Returns the transliterated, lower-case value, or null if characters outside
    /// <c>[a-z]</c> remain (→ NeedsInput).
    /// </summary>
    public static string? TryTransliterate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var lower = value.Trim().ToLowerInvariant();
        var mapped = new StringBuilder(lower.Length + 4);
        foreach (var ch in lower)
        {
            if (Dropped.Contains(ch))
            {
                continue;
            }

            if (Map.TryGetValue(ch, out var replacement))
            {
                mapped.Append(replacement);
            }
            else
            {
                mapped.Append(ch);
            }
        }

        var decomposed = mapped.ToString().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (ch is < 'a' or > 'z')
            {
                return null;
            }

            result.Append(ch);
        }

        return result.Length == 0 ? null : result.ToString();
    }
}
