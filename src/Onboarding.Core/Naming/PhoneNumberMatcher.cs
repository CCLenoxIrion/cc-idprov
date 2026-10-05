namespace Onboarding.Core.Naming;

/// <summary>
/// Compares phone numbers stored in arbitrary display formats with an E.164 number (DECISIONS
/// X9). AD keeps <c>telephoneNumber</c> as free text, e.g. "+49 7465 929678-0",
/// "+49 (7465) 929678 0" or "074659296780".
/// </summary>
public static class PhoneNumberMatcher
{
    /// <summary>Country codes have 1–3 digits (ITU-T E.164).</summary>
    private const int MaxCountryCodeLength = 3;

    public static bool Matches(string? stored, string e164)
    {
        ArgumentNullException.ThrowIfNull(e164);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var target = Digits(e164);
        if (target.Length == 0)
        {
            return false;
        }

        // "+49 (0) 7465 …": the bracketed trunk prefix is not dialled from abroad.
        var raw = stored.Replace("(0)", "", StringComparison.Ordinal).Trim();
        var digits = Digits(raw);
        if (raw.StartsWith('+'))
        {
            return digits == target;
        }

        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            return digits[2..] == target;
        }

        if (digits.StartsWith('0'))
        {
            // National format: trunk prefix 0 replaces the country code.
            var national = digits[1..];
            var countryCodeLength = target.Length - national.Length;
            return national.Length > 0 &&
                   countryCodeLength is >= 1 and <= MaxCountryCodeLength &&
                   target.EndsWith(national, StringComparison.Ordinal);
        }

        return digits == target;
    }

    /// <summary>All ASCII digits of the value, in order.</summary>
    public static string Digits(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Concat(value.Where(char.IsAsciiDigit));
    }
}
