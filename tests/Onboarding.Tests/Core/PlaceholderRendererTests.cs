using Onboarding.Core.Naming;

namespace Onboarding.Tests.Core;

public sealed class PlaceholderRendererTests
{
    [Fact]
    public void Renders_known_placeholders()
    {
        var values = new Dictionary<string, string> { ["sam"] = "lirion" };

        Assert.Equal(@"\\dc01\lirion$", PlaceholderRenderer.Render(@"\\dc01\{sam}$", values));
    }

    [Fact]
    public void Unknown_placeholder_throws()
    {
        Assert.Throws<TemplateException>(() => PlaceholderRenderer.Render("{foo}", new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("{sam")]
    [InlineData("sam}")]
    public void Malformed_template_throws(string template)
    {
        Assert.Throws<TemplateException>(() => PlaceholderRenderer.Validate(template));
    }

    [Fact]
    public void Known_placeholder_without_value_fails_softly()
    {
        var ok = PlaceholderRenderer.TryRender("{lastName}.x", new Dictionary<string, string>(), out _, out var missing);

        Assert.False(ok);
        Assert.Equal("lastName", missing);
    }
}
