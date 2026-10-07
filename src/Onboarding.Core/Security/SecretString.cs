namespace Onboarding.Core.Security;

/// <summary>
/// Holds a password in memory. <see cref="ToString"/> is redacted so the value cannot end up in
/// logs, exceptions or interpolated strings by accident (SPEC §9). Use <see cref="Reveal"/> only
/// where the plaintext is needed (encryption, stdin of the step process).
/// </summary>
public sealed class SecretString
{
    public const string Redacted = "***";

    private readonly string _value;

    public SecretString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public int Length => _value.Length;

    public string Reveal() => _value;

    public override string ToString() => Redacted;
}

/// <summary>Encrypts secrets for the worker (web side, public key only; DECISIONS P1).</summary>
public interface ISecretEncryptor
{
    byte[] Encrypt(SecretString secret);
}

/// <summary>Decrypts secrets (worker side, non-exportable private key; DECISIONS P1).</summary>
public interface ISecretDecryptor
{
    SecretString Decrypt(byte[] ciphertext);
}
