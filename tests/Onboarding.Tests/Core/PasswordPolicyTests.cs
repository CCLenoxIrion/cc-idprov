using Onboarding.Core.Security;

namespace Onboarding.Tests.Core;

public sealed class PasswordPolicyTests
{
    private static readonly PasswordPolicy Policy = new(12, ComplexityEnabled: true);

    [Fact]
    public void Secret_string_is_redacted()
    {
        var secret = new SecretString("Geheim!12345");

        Assert.Equal("***", secret.ToString());
        Assert.Equal("***", $"{secret}");
        Assert.Equal("Geheim!12345", secret.Reveal());
    }

    [Theory]
    [InlineData("Sommer2026!x", true)]
    [InlineData("kurz1!A", false)] // too short
    [InlineData("nurkleinbuchstaben", false)] // one category
    [InlineData("Lirion2026!abc", false)] // contains sam
    [InlineData("Lenox2026!abcd", false)] // contains display name token
    public void Validates(string password, bool valid)
    {
        var errors = PasswordPolicyValidator.Validate(new SecretString(password), Policy, "lirion", "Lenox Irion");

        Assert.Equal(valid, errors.Count == 0);
        Assert.All(errors, e => Assert.DoesNotContain(password, e, StringComparison.Ordinal));
    }

    [Fact]
    public void Generated_password_satisfies_policy()
    {
        for (var i = 0; i < 50; i++)
        {
            var password = PasswordPolicyValidator.Generate(Policy);
            Assert.Empty(PasswordPolicyValidator.Validate(password, Policy, "lirion", "Lenox Irion"));
            Assert.True(password.Length >= 16);
        }
    }
}
