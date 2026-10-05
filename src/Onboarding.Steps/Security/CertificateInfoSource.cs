using System.Security.Cryptography.X509Certificates;

namespace Onboarding.Steps.Security;

/// <summary>Expiry of a certificate in the machine store (DECISIONS X15).</summary>
public sealed record CertificateInfo(DateTimeOffset NotAfter);

public interface ICertificateInfoSource
{
    /// <summary>The certificate with this thumbprint, or null if it is not in <c>LocalMachine\My</c>.</summary>
    CertificateInfo? Find(string thumbprint);
}

public sealed class StoreCertificateInfoSource : ICertificateInfoSource
{
    public CertificateInfo? Find(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", "", StringComparison.Ordinal), validOnly: false);
        try
        {
            return found.Count == 0 ? null : new CertificateInfo(new DateTimeOffset(found[0].NotAfter.ToUniversalTime(), TimeSpan.Zero));
        }
        finally
        {
            foreach (var certificate in found)
            {
                certificate.Dispose();
            }
        }
    }
}
