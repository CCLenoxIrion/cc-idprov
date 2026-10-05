using System.Globalization;

namespace Onboarding.Web.Components.Common;

/// <summary>Helpers for editing lists and time spans in text inputs.</summary>
public static class Lines
{
    public static string Join(IEnumerable<string> values) => string.Join("\n", values);

    public static List<string> Split(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    public static string Format(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out TimeSpan value) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value);
}
