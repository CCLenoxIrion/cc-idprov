using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Onboarding.Core.Security;

namespace Onboarding.Steps.Security;

/// <summary>
/// Envelope encryption for the initial password (DECISIONS P1): a random AES-256-GCM key
/// encrypts the secret, the RSA public key (OAEP-SHA256) wraps the AES key. The web host needs
/// only the public key; decryption needs the worker's private key.
/// </summary>
/// <remarks>
/// Format: version (1 byte) | wrapped key length (2 bytes, big endian) | wrapped key | nonce (12)
/// | tag (16) | ciphertext.
/// </remarks>
public sealed class HybridSecretProtector(IRsaKeySource keySource) : ISecretEncryptor, ISecretDecryptor
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public byte[] Encrypt(SecretString secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        using var rsa = keySource.GetPublicKey();

        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(secret.Reveal());
        try
        {
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            var wrapped = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
            var result = new byte[1 + 2 + wrapped.Length + NonceSize + TagSize + ciphertext.Length];
            var span = result.AsSpan();
            span[0] = FormatVersion;
            BinaryPrimitives.WriteUInt16BigEndian(span[1..], checked((ushort)wrapped.Length));
            var offset = 3;
            wrapped.CopyTo(span[offset..]);
            offset += wrapped.Length;
            nonce.CopyTo(span[offset..]);
            offset += NonceSize;
            tag.CopyTo(span[offset..]);
            offset += TagSize;
            ciphertext.CopyTo(span[offset..]);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public SecretString Decrypt(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (ciphertext.Length < 3 + NonceSize + TagSize || ciphertext[0] != FormatVersion)
        {
            throw new CryptographicException("Unsupported secret format.");
        }

        var span = ciphertext.AsSpan();
        int wrappedLength = BinaryPrimitives.ReadUInt16BigEndian(span[1..]);
        var offset = 3;
        if (ciphertext.Length < offset + wrappedLength + NonceSize + TagSize)
        {
            throw new CryptographicException("Truncated secret.");
        }

        var wrapped = span.Slice(offset, wrappedLength).ToArray();
        offset += wrappedLength;
        var nonce = span.Slice(offset, NonceSize);
        offset += NonceSize;
        var tag = span.Slice(offset, TagSize);
        offset += TagSize;
        var encrypted = span[offset..];

        using var rsa = keySource.GetPrivateKey();
        var key = rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
        var plaintext = new byte[encrypted.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, encrypted, tag, plaintext);
            return new SecretString(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
