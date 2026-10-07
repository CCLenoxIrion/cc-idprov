using System.Globalization;
using System.Text;
using Onboarding.Core.Domain;

namespace Onboarding.Web.Services;

/// <summary>CSV export of the audit log (SPEC §9). Semicolon-separated, UTF-8 with BOM for Excel.</summary>
public static class AuditCsv
{
    public static byte[] Write(IEnumerable<AuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new StringBuilder();
        sb.Append("Zeit (UTC);Akteur;Auftrag;Step;Aktion;Ergebnis;Details\r\n");
        foreach (var e in entries)
        {
            sb.Append(Field(e.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(';')
                .Append(Field(e.Actor)).Append(';')
                .Append(Field(e.RequestId?.ToString())).Append(';')
                .Append(Field(e.StepKey)).Append(';')
                .Append(Field(e.Action)).Append(';')
                .Append(Field(e.Result)).Append(';')
                .Append(Field(e.Details)).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        // Neutralize spreadsheet formulas (CSV injection).
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([';', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
