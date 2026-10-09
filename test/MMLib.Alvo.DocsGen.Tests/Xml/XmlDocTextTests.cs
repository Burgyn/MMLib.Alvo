using MMLib.Alvo.DocsGen.Xml;
using System.Xml.Linq;

namespace MMLib.Alvo.DocsGen.Tests.Xml;

public class XmlDocTextTests
{
    [Fact]
    public void Inline_elements_become_markdown() =>
        XmlDocText.ToMarkdown(XElement.Parse(
            "<summary>Use <c>AddAlvo</c> or <see cref=\"T:MMLib.Alvo.IAlvoBuilder\"/>; <see langword=\"null\"/> means\n   none, see <paramref name=\"name\"/> and <b>this</b>.</summary>"))
            .ShouldBe("Use `AddAlvo` or `IAlvoBuilder`; `null` means none, see `name` and **this**.");

    [Fact]
    public void Paragraphs_are_separated() =>
        XmlDocText.ToMarkdown(XElement.Parse("<remarks><para>One.</para><para>Two.</para></remarks>")).ShouldBe("One.\n\nTwo.");

    [Fact]
    public void A_method_cref_shows_its_name_only() =>
        XmlDocText.ToMarkdown(XElement.Parse("<summary><see cref=\"M:MMLib.Alvo.Management.IAlvoManagement.GetCelFunctionsAsync(System.String,System.Threading.CancellationToken)\"/></summary>"))
            .ShouldBe("`GetCelFunctionsAsync`");

    [Fact]
    public void A_generic_type_cref_drops_its_arity() =>
        XmlDocText.ToMarkdown(XElement.Parse("<summary><see cref=\"T:System.Collections.Generic.List`1\"/></summary>")).ShouldBe("`List`");

    [Fact]
    public void A_code_block_keeps_its_indentation() =>
        XmlDocText.ToMarkdown(XElement.Parse("<remarks>Before.<code>\n    if (a)\n        b();\n</code>After.</remarks>"))
            .ShouldBe("Before.\n\n```csharp\nif (a)\n    b();\n```\n\nAfter.");
}
