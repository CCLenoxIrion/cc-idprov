using System.Globalization;

namespace Onboarding.Core.Operations;

/// <summary>
/// Last known state of a certificate the service depends on (SPEC §9, DECISIONS X15).
/// Operational state written by the worker, not audited; one row per <see cref="Name"/>.
/// </summary>
public sealed class CertificateStatus
{
    /// <summary>Stable key, see <see cref="CertificateNames"/>.</summary>
    public string Name { get; set; } = "";

    public string Thumbprint { get; set; } = "";

    /// <summary>Found in <c>LocalMachine\My</c> on the checking host.</summary>
    public bool Found { get; set; }

    public DateTimeOffset? NotAfter { get; set; }

    public DateTimeOffset CheckedAt { get; set; }
}

public static class CertificateNames
{
    /// <summary>Worker certificate for the initial password (DECISIONS P1/W2).</summary>
    public const string PasswordCertificate = "PasswordCertificate";

    /// <summary>App registration of the worker for Graph/EXO/Teams (X12).</summary>
    public const string CloudApp = "CloudApp";

    /// <summary>Read-only app registration of the web (X16); checked by the web itself.</summary>
    public const string GraphRead = "GraphRead";

    public static string DisplayName(string name) => name switch
    {
        PasswordCertificate => "Passwort-Zertifikat (Worker)",
        CloudApp => "Zertifikat der Cloud-App-Registrierung (Worker)",
        GraphRead => "Zertifikat der Graph-Lese-App (Web)",
        _ => name,
    };
}

public sealed record CertificateWarning(string Name, string Message, bool Critical);

/// <summary>Turns certificate states into UI warnings (X15).</summary>
public static class CertificateWarnings
{
    public static IReadOnlyList<CertificateWarning> Evaluate(
        IEnumerable<CertificateStatus> statuses,
        DateTimeOffset now,
        int warningDays,
        TimeSpan maxCheckAge,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(zone);
        var warnings = new List<CertificateWarning>();
        foreach (var status in statuses.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var name = CertificateNames.DisplayName(status.Name);
            if (now - status.CheckedAt > maxCheckAge)
            {
                warnings.Add(new(name, $"{name}: letzte Prüfung am {Format(status.CheckedAt, zone)} – läuft der Worker?", Critical: false));
            }

            if (!status.Found || status.NotAfter is null)
            {
                warnings.Add(new(name, $"{name}: nicht im Zertifikatsspeicher gefunden (Thumbprint {Short(status.Thumbprint)}).", Critical: true));
                continue;
            }

            var notAfter = status.NotAfter.Value;
            if (notAfter <= now)
            {
                warnings.Add(new(name, $"{name}: abgelaufen seit {Format(notAfter, zone)}.", Critical: true));
            }
            else if (notAfter - now <= TimeSpan.FromDays(warningDays))
            {
                var days = (int)Math.Ceiling((notAfter - now).TotalDays);
                warnings.Add(new(name, $"{name}: läuft am {Format(notAfter, zone)} ab (in {days} Tagen).", Critical: days <= 7));
            }
        }

        return warnings;
    }

    private static string Format(DateTimeOffset value, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Short(string thumbprint) =>
        string.IsNullOrWhiteSpace(thumbprint) ? "nicht konfiguriert" : thumbprint.Length > 8 ? thumbprint[..8] + "…" : thumbprint;
}
