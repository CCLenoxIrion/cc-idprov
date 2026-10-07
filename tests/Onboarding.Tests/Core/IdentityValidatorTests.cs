using Onboarding.Core.Naming;

namespace Onboarding.Tests.Core;

public sealed class IdentityValidatorTests
{
    [Theory]
    [InlineData("lirion")]
    [InlineData("mmueller2")]
    [InlineData("a")]
    [InlineData("abcdefghijklmnopqrst")] // 20
    public void Valid_sam(string sam) => Assert.Null(IdentityValidator.ValidateSam(sam));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("l.irion")]
    [InlineData("l-irion")]
    [InlineData("LIrion")]
    [InlineData("müller")]
    [InlineData("l irion")]
    [InlineData("abcdefghijklmnopqrstu")] // 21
    public void Invalid_sam(string? sam) => Assert.NotNull(IdentityValidator.ValidateSam(sam));

    [Theory]
    [InlineData("l.irion")]
    [InlineData("m.mueller-luedenscheidt")]
    [InlineData("vertrieb")]
    [InlineData("a1.b-2")]
    public void Valid_mail_local_part(string local) => Assert.Null(IdentityValidator.ValidateMailLocalPart(local));

    [Theory]
    [InlineData("")]
    [InlineData(".irion")]
    [InlineData("irion.")]
    [InlineData("-irion")]
    [InlineData("irion-")]
    [InlineData("l..irion")]
    [InlineData("l_irion")]
    [InlineData("L.Irion")]
    [InlineData("l.irion+x")]
    [InlineData("m.müller")]
    public void Invalid_mail_local_part(string local) => Assert.NotNull(IdentityValidator.ValidateMailLocalPart(local));

    [Theory]
    [InlineData("l.irion@cleancontrolling.de", true)]
    [InlineData("l.irion@clean-controlling.de", true)]
    [InlineData("l.irion@localhost", false)]
    [InlineData("l.irion@@cleancontrolling.de", false)]
    [InlineData("l.irion@-x.de", false)]
    [InlineData("l.irion", false)]
    public void Mail(string mail, bool valid) => Assert.Equal(valid, IdentityValidator.ValidateMail(mail) is null);

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", true)]
    [InlineData("9999", true)]
    [InlineData("10000", false)]
    [InlineData("1a", false)]
    [InlineData(" 1", false)]
    public void Extension(string? extension, bool valid) =>
        Assert.Equal(valid, IdentityValidator.ValidateExtension(extension) is null);
}
