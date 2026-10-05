namespace Onboarding.Core.Domain;

/// <summary>Values derived from the input and configuration (SPEC §2).</summary>
public sealed record DerivedIdentity
{
    public required string SamAccountName { get; init; }
    public required string Mail { get; init; }
    public required string UserPrincipalName { get; init; }
    public required IReadOnlyList<string> ProxyAddresses { get; init; }
    public required string GivenName { get; init; }
    public required string Surname { get; init; }
    public required string DisplayName { get; init; }
    public required string Department { get; init; }
    public required string Company { get; init; }
    public required string OuDistinguishedName { get; init; }
    public required string ScriptPath { get; init; }
    public required string HomeUnc { get; init; }

    /// <summary>Extension (digits only), null without extension.</summary>
    public string? Extension { get; init; }

    /// <summary>E.164 number, null without extension.</summary>
    public string? PhoneE164 { get; init; }

    /// <summary>Display value for AD <c>telephoneNumber</c>, null without extension.</summary>
    public string? TelephoneNumber { get; init; }

    /// <summary>Additional AD attributes, e.g. the doctor title attribute. Absent = not set.</summary>
    public IReadOnlyDictionary<string, string> AdditionalAttributes { get; init; } =
        new Dictionary<string, string>();
}
