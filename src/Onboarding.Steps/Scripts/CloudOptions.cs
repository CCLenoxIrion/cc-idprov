using System.Globalization;
using System.Text.RegularExpressions;
using Onboarding.Core.Steps;

namespace Onboarding.Steps.Scripts;

/// <summary>
/// App registration of the worker for Graph, Exchange Online and Teams (<c>Integrations:Cloud</c>,
/// DECISIONS X12). Identifiers only – the certificate's private key stays in the store.
/// </summary>
public sealed partial class CloudOptions
{
    /// <summary>Steps that may be switched to ManualTask (SPEC §3: app-only support to verify, X13).</summary>
    public static IReadOnlySet<string> ManualCapableSteps { get; } =
        new HashSet<string>(StringComparer.Ordinal) { StepKeys.TeamsVoicemail, StepKeys.TeamsForwarding };

    public string TenantId { get; set; } = "";
    public string AppId { get; set; } = "";

    /// <summary>SHA-1 thumbprint of the certificate in <c>LocalMachine\My</c>.</summary>
    public string CertificateThumbprint { get; set; } = "";

    /// <summary>Exchange Online organization, e.g. <c>contoso.onmicrosoft.com</c>.</summary>
    public string ExchangeOrganization { get; set; } = "";

    /// <summary>Steps that return a ManualTask with a ready command instead of running.</summary>
    public List<string> ManualSteps { get; set; } = [];

    /// <summary>Throws a configuration error naming the invalid key (never values).</summary>
    public void Validate()
    {
        if (!Guid.TryParse(TenantId, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException("Integrations:Cloud:TenantId must be a GUID.");
        }

        if (!Guid.TryParse(AppId, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException("Integrations:Cloud:AppId must be a GUID.");
        }

        if (!Thumbprint().IsMatch(CertificateThumbprint))
        {
            throw new InvalidOperationException("Integrations:Cloud:CertificateThumbprint must be 40 hex characters.");
        }

        if (!Organization().IsMatch(ExchangeOrganization))
        {
            throw new InvalidOperationException("Integrations:Cloud:ExchangeOrganization must be '<name>.onmicrosoft.com'.");
        }

        if (ManualSteps.FirstOrDefault(s => !ManualCapableSteps.Contains(s)) is { } invalid)
        {
            throw new InvalidOperationException(
                $"Integrations:Cloud:ManualSteps '{invalid}' is not allowed ({string.Join(", ", ManualCapableSteps)}).");
        }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{40}$")]
    private static partial Regex Thumbprint();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*\.onmicrosoft\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex Organization();
}
