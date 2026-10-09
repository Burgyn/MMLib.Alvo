using MMLib.Alvo.DocsGen.Markdown;

namespace MMLib.Alvo.DocsGen.Tests.Markdown;

public class MdTests
{
    [Theory]
    [InlineData("before* = in-transaction; after* = post-commit", @"before\* = in-transaction; after\* = post-commit")]
    [InlineData("a <b> c", "a &lt;b&gt; c")]
    [InlineData("snake_case_name", @"snake\_case\_name")]
    [InlineData("line one\n   line two", "line one line two")]
    [InlineData("{{@user.email}} stays", "{{@user.email}} stays")]
    [InlineData("the `tenant_id` and `a<b>` spans_stay", @"the `tenant_id` and `a<b>` spans\_stay")]
    [InlineData("an unclosed `back_tick", @"an unclosed `back\_tick")]
    public void Text_renders_literally(string input, string expected) => Md.Text(input).ShouldBe(expected);

    [Fact]
    public void Cell_escapes_pipes() => Md.Cell("a || b").ShouldBe(@"a \|\| b");

    [Fact]
    public void CodeCell_escapes_pipes_inside_the_code_span() => Md.CodeCell("a || b").ShouldBe(@"`a \|\| b`");

    [Fact]
    public void Code_widens_the_fence_when_the_text_has_a_backtick() => Md.Code("a`b").ShouldBe("`` a`b ``");

    [Fact]
    public void Frontmatter_quotes_yaml_and_orders_the_sidebar() =>
        Md.Frontmatter("A \"quoted\" title", "Desc: with colon", 3).ShouldBe(
            "---\ntitle: \"A \\\"quoted\\\" title\"\ndescription: \"Desc: with colon\"\nsidebar:\n  order: 3\n---\n\n");

    [Fact]
    public void Frontmatter_sets_the_sidebar_label_and_caps_the_toc() =>
        Md.Frontmatter("entities.fields", "Fields.", 2, sidebarLabel: "entities · fields", tocMaxHeadingLevel: 2).ShouldBe(
            "---\ntitle: \"entities.fields\"\ndescription: \"Fields.\"\nsidebar:\n  order: 2\n  label: \"entities · fields\"\ntableOfContents:\n  maxHeadingLevel: 2\n---\n\n");
}
