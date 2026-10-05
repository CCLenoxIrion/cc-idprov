using System.Security.Cryptography;
using System.Text;
using Onboarding.Core.Security;
using Onboarding.Steps.Security;

namespace Onboarding.Tests.Steps;

public sealed class HybridSecretProtectorTests
{
    private sealed class InMemoryKeySource : IRsaKeySource, IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);
        public bool PrivateKeyAvailable { get; set; } = true;

        public RSA GetPublicKey()
        {
            var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(_key.ExportSubjectPublicKeyInfo(), out _);
            return rsa;
        }

        public RSA GetPrivateKey()
        {
            if (!PrivateKeyAvailable)
            {
                throw new CryptographicException("no private key");
            }

            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(_key.ExportPkcs8PrivateKey(), out _);
            return rsa;
        }

        public void Dispose() => _key.Dispose();
    }

    [Fact]
    public void Roundtrip_and_no_plaintext_in_ciphertext()
    {
        using var keys = new InMemoryKeySource();
        var protector = new HybridSecretProtector(keys);
        const string password = "Sommer2026!Geheim-äöü";

        var ciphertext = protector.Encrypt(new SecretString(password));

        Assert.Equal(1, ciphertext[0]);
        Assert.DoesNotContain(Encoding.UTF8.GetString(ciphertext), password, StringComparison.Ordinal);
        Assert.Equal(password, protector.Decrypt(ciphertext).Reveal());
    }

    [Fact]
    public void Same_secret_encrypts_differently()
    {
        using var keys = new InMemoryKeySource();
        var protector = new HybridSecretProtector(keys);

        Assert.NotEqual(protector.Encrypt(new SecretString("a")), protector.Encrypt(new SecretString("a")));
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        using var keys = new InMemoryKeySource();
        var protector = new HybridSecretProtector(keys);
        var ciphertext = protector.Encrypt(new SecretString("Sommer2026!x"));
        ciphertext[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Decrypt(ciphertext));
    }

    [Fact]
    public void Web_host_without_private_key_can_encrypt_but_not_decrypt()
    {
        using var keys = new InMemoryKeySource { PrivateKeyAvailable = false };
        var protector = new HybridSecretProtector(keys);

        var ciphertext = protector.Encrypt(new SecretString("Sommer2026!x"));

        Assert.ThrowsAny<CryptographicException>(() => protector.Decrypt(ciphertext));
    }
}
