using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Onboarding.Steps.Security;

/// <summary>Provides the RSA key pair used for <see cref="HybridSecretProtector"/>.</summary>
public interface IRsaKeySource
{
    RSA GetPublicKey();

    /// <summary>Only available on the worker host.</summary>
    RSA GetPrivateKey();
}

/// <summary>
/// Worker certificate from the Windows certificate store (LocalMachine\My), selected by
/// thumbprint. The thumbprint is read on every call so config changes apply immediately.
/// </summary>
public sealed class CertificateStoreKeySource(Func<string> thumbprint) : IRsaKeySource
{
    public RSA GetPublicKey()
    {
        using var cert = Find();
        return cert.GetRSAPublicKey() ?? throw new CryptographicException("Certificate has no RSA public key.");
    }

    public RSA GetPrivateKey()
    {
        using var cert = Find();
        return cert.GetRSAPrivateKey() ?? throw new CryptographicException("Private key of the worker certificate is not available on this host.");
    }

    private X509Certificate2 Find()
    {
        var value = thumbprint();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Thumbprint of the worker certificate (PasswordCertThumbprint) is not configured.");
        }

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, value.Replace(" ", "", StringComparison.Ordinal), validOnly: false);
        if (found.Count == 0)
        {
            throw new InvalidOperationException($"Worker certificate {value} not found in LocalMachine\\My.");
        }

        var cert = found[0];
        for (var i = 1; i < found.Count; i++)
        {
            found[i].Dispose();
        }

        return cert;
    }
}

/// <summary>
/// Development only: RSA key in a local PEM file, created on first use. Never use in production.
/// </summary>
public sealed class DevelopmentPemKeySource(string path) : IRsaKeySource
{
    private readonly Lock _lock = new();

    public RSA GetPublicKey() => Load();

    public RSA GetPrivateKey() => Load();

    private RSA Load()
    {
        lock (_lock)
        {
            var rsa = RSA.Create(3072);
            if (File.Exists(path))
            {
                rsa.ImportFromPem(File.ReadAllText(path));
                return rsa;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
            return rsa;
        }
    }
}
