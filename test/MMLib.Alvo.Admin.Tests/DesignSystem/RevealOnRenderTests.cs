using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>A row revealed by its id is matched by a selector the browser accepts, whatever the id holds (final review M5).</summary>
public sealed class RevealOnRenderTests
{
    [Theory]
    [InlineData("endpoint-billing", "[id=\"endpoint-billing\"]")]
    [InlineData("endpoint-a\"b", "[id=\"endpoint-a\\\"b\"]")]
    [InlineData("endpoint-a\\b", "[id=\"endpoint-a\\\\b\"]")]
    [InlineData("endpoint-a]b#c.d e", "[id=\"endpoint-a]b#c.d e\"]")]
    [InlineData("endpoint-a\nb", "[id=\"endpoint-a\\a b\"]")]
    public void An_id_is_quoted_and_escaped_as_a_css_string(string id, string selector)
        => RevealOnRender.IdSelector(id).ShouldBe(selector);
}
