using Onboarding.Core.Domain;

namespace Onboarding.Core.Naming;

public enum IdentityIssueCode
{
    MissingName,

    /// <summary>Hyphen or whitespace in first or last name (DECISIONS D4).</summary>
    DoubleName,

    /// <summary>Characters outside [a-z] after transliteration (DECISIONS D2).</summary>
    NotTransliterable,

    /// <summary>Derived sam longer than 20 characters (DECISIONS D5).</summary>
    SamTooLong,
    InvalidSam,
    InvalidMail,
    InvalidExtension,

    /// <summary>A template uses a placeholder that has no value for this request.</summary>
    TemplateValueMissing,

    /// <summary>proxyAddresses templates do not yield exactly one primary equal to mail.</summary>
    PrimaryProxyMismatch,
    Collision,
}

/// <summary>Reason why a request needs manual input instead of an automatic derivation.</summary>
public sealed record IdentityIssue(IdentityIssueCode Code, string Message);

/// <summary>Result of <see cref="IdentityDeriver.Derive"/>.</summary>
public sealed record DerivationResult(DerivedIdentity? Identity, IReadOnlyList<IdentityIssue> Issues)
{
    public bool NeedsInput => Identity is null || Issues.Count > 0;
}

/// <summary>Derivation plus collision check; what workflow actions consume.</summary>
public sealed record IdentityCheck(DerivedIdentity? Identity, IReadOnlyList<IdentityIssue> Issues)
{
    public bool NeedsInput => Identity is null || Issues.Count > 0;

    public string Summary => string.Join(" ", Issues.Select(i => i.Message));

    public static IdentityCheck From(DerivationResult derivation, IReadOnlyList<Collision> collisions)
    {
        ArgumentNullException.ThrowIfNull(derivation);
        ArgumentNullException.ThrowIfNull(collisions);
        var issues = derivation.Issues
            .Concat(collisions.Select(c => new IdentityIssue(IdentityIssueCode.Collision, c.Description)))
            .ToList();
        return new IdentityCheck(derivation.Identity, issues);
    }
}
