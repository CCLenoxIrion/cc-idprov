namespace Onboarding.Core.Domain;

/// <summary>
/// sAMAccountName and mail assigned manually by an ITAdmin to resolve <c>NeedsInput</c>
/// (double names, collisions, names that cannot be transliterated).
/// </summary>
public sealed record IdentityOverride(string SamAccountName, string Mail);
