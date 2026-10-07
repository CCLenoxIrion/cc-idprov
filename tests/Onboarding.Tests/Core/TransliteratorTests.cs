using Onboarding.Core.Naming;

namespace Onboarding.Tests.Core;

public sealed class TransliteratorTests
{
    [Theory]
    [InlineData("Irion", "irion")]
    [InlineData("Müller", "mueller")]
    [InlineData("Größmann", "groessmann")]
    [InlineData("Jäger", "jaeger")]
    [InlineData("Özlem", "oezlem")]
    [InlineData("ÄÖÜ", "aeoeue")]
    [InlineData("Weiß", "weiss")]
    [InlineData("STRAẞE", "strasse")] // capital sharp s
    [InlineData("Søren", "soren")]
    [InlineData("SØREN", "soren")]
    [InlineData("Ærø", "aero")]
    [InlineData("Œuvre", "oeuvre")]
    [InlineData("œ", "oe")]
    [InlineData("Łukasz", "lukasz")]
    [InlineData("łódź", "lodz")]
    [InlineData("Đorđe", "dorde")]
    [InlineData("José", "jose")]
    [InlineData("Núñez", "nunez")]
    [InlineData("François", "francois")]
    [InlineData("Dvořák", "dvorak")]
    [InlineData("O'Brien", "obrien")]
    [InlineData("D’Angelo", "dangelo")]
    [InlineData("  Irion  ", "irion")]
    public void Transliterates_to_lowercase_ascii(string input, string expected)
    {
        Assert.Equal(expected, Transliterator.TryTransliterate(input));
    }

    [Theory]
    [InlineData("Иван")] // Cyrillic
    [InlineData("李")] // CJK
    [InlineData("Ali1")] // digit
    [InlineData("Anna.Lena")] // punctuation
    [InlineData("ı")] // Turkish dotless i: not decomposable
    [InlineData("")]
    [InlineData("'")]
    public void Returns_null_when_characters_remain_outside_a_to_z(string input)
    {
        Assert.Null(Transliterator.TryTransliterate(input));
    }
}
