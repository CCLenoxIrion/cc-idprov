using System.Security.Cryptography;

namespace Onboarding.Core.Security;

/// <summary>
/// Domain password policy as read from <c>Get-ADDefaultDomainPasswordPolicy</c> (SPEC §2).
/// </summary>
public sealed record PasswordPolicy(int MinLength, bool ComplexityEnabled);

/// <summary>Checks and generates initial passwords. Messages never contain the password.</summary>
public static class PasswordPolicyValidator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!#$%&*+-=?@_";

    /// <summary>
    /// Validates against the policy. With complexity enabled, AD requires three of the four
    /// categories and that neither the sAMAccountName nor display-name tokens (≥ 3 chars) occur.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        SecretString password,
        PasswordPolicy policy,
        string? samAccountName,
        string? displayName)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(policy);

        var errors = new List<string>();
        var value = password.Reveal();
        if (value.Length < policy.MinLength)
        {
            errors.Add($"Mindestens {policy.MinLength} Zeichen erforderlich.");
        }

        if (!policy.ComplexityEnabled)
        {
            return errors;
        }

        var categories = 0;
        categories += value.Any(char.IsUpper) ? 1 : 0;
        categories += value.Any(char.IsLower) ? 1 : 0;
        categories += value.Any(char.IsDigit) ? 1 : 0;
        categories += value.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0;
        if (categories < 3)
        {
            errors.Add("Mindestens drei der vier Kategorien (Groß-, Kleinbuchstaben, Ziffern, Sonderzeichen) erforderlich.");
        }

        if (samAccountName is { Length: >= 3 } && value.Contains(samAccountName, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Darf den Kontonamen nicht enthalten.");
        }

        if (displayName is not null)
        {
            var separators = new[] { ' ', ',', '.', '-', '_', '#', '\t' };
            if (displayName.Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Any(token => token.Length >= 3 && value.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("Darf keine Teile des Anzeigenamens enthalten.");
            }
        }

        return errors;
    }

    /// <summary>Generates a random password with all four categories (button "Generieren").</summary>
    public static SecretString Generate(PasswordPolicy policy, int minimumLength = 16)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var length = Math.Max(policy.MinLength, minimumLength);
        var all = Upper + Lower + Digits + Symbols;
        var chars = new char[length];
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Symbols);
        for (var i = 4; i < length; i++)
        {
            chars[i] = Pick(all);
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new SecretString(new string(chars));
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
