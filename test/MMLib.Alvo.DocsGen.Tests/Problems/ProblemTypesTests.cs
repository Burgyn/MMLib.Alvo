using MMLib.Alvo.Api;
using MMLib.Alvo.DocsGen.Problems;
using MMLib.Alvo.DocsGen.Xml;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Tests.Problems;

public partial class ProblemTypesTests
{
    private const string Xml = """
        <doc><members>
          <member name="F:MMLib.Alvo.Api.AlvoProblemTypes.Validation"><summary>Schema-derived validation refused the request body (422).</summary></member>
          <member name="F:MMLib.Alvo.Api.AlvoProblemTypes.MalformedQuery"><summary>The query string or the request body is malformed (422) — the shape is wrong, nothing is hidden.</summary></member>
          <member name="F:MMLib.Alvo.Api.AlvoProblemTypes.UnreadableRequest"><summary>The server refused the request before Alvo could read it (400, 408 or 413).</summary></member>
        </members></doc>
        """;

    private static readonly ProblemTypeNote _note = new(
        ["A required field is missing."], "Send every required field.", ["required", "max-length"], ["guides/write-data"], NeedsProblemDetails: false);

    [Fact]
    public void Status_and_meaning_come_from_the_summary()
    {
        var catalog = ProblemTypeCatalog.Read(XmlDocs.Parse(Xml));

        catalog.Single(p => p.Slug == "validation").ShouldBe(new ProblemType("validation", "https://alvo.dev/errors/validation", 422, "Schema-derived validation refused the request body."));
        catalog.Single(p => p.Slug == "malformed-query").Meaning.ShouldBe("The query string or the request body is malformed — the shape is wrong, nothing is hidden.");
    }

    [Fact]
    public void A_status_list_is_kept_whole()
    {
        var unreadable = ProblemTypeCatalog.Read(XmlDocs.Parse(Xml)).Single(p => p.Slug == "unreadable-request");

        unreadable.Status.ShouldBe(400);
        unreadable.StatusLabel.ShouldBe("400, 408 or 413");
        unreadable.Meaning.ShouldBe("The server refused the request before Alvo could read it.");
    }

    [Fact]
    public void A_slug_missing_from_the_xml_is_still_listed()
    {
        var forbidden = ProblemTypeCatalog.Read(XmlDocs.Parse(Xml)).Single(p => p.Slug == "forbidden");

        forbidden.Status.ShouldBeNull();
        forbidden.Meaning.ShouldBeEmpty();
        forbidden.StatusLabel.ShouldBe("—");
    }

    [Fact]
    public void Each_slug_is_its_own_section()
    {
        var page = ProblemTypesGenerator.Render(
            [new ProblemType("validation", "https://alvo.dev/errors/validation", 422, "Refused.")],
            new Dictionary<string, ProblemTypeNote> { ["validation"] = _note },
            _ => "Write data safely");

        page.RelativePath.ShouldBe("problem-types.md");
        page.Content.ShouldContain(
            "## `validation`\n\n" +
            "**Status:** 422 · **`type`:** `https://alvo.dev/errors/validation` · **Returned by:** every host\n\n" +
            "Refused.\n\n" +
            "**Causes**\n\n- A required field is missing.\n\n" +
            "**Fix:** Send every required field.\n\n" +
            "**Violation codes:** `required`, `max-length`\n\n" +
            "**Guides:** [Write data safely](/guides/write-data/)\n");
    }

    [Fact]
    public void An_opt_in_slug_says_which_hosts_return_it()
    {
        var page = ProblemTypesGenerator.Render(
            [new ProblemType("internal", "https://alvo.dev/errors/internal", 500, "Unexpected.")],
            new Dictionary<string, ProblemTypeNote> { ["internal"] = _note with { ViolationCodes = [], NeedsProblemDetails = true } },
            _ => "Handle errors");

        page.Content.ShouldContain("**Returned by:** the standalone host; an embedded host only with `AddAlvoProblemDetails()` and `UseExceptionHandler()`");
        page.Content.ShouldContain("**Violation codes:** none — this refusal carries no itemised reasons");
    }

    [Theory]
    [InlineData(false, false, "every host")]
    [InlineData(true, false, "the standalone host; an embedded host only with `AddAlvoProblemDetails()` and `UseExceptionHandler()`")]
    [InlineData(false, true, "the Management API only, on any host that maps it — never a Data API route")]
    public void Returned_by_is_per_slug(bool needsProblemDetails, bool managementApiOnly, string expected) =>
        ProblemTypesGenerator.ReturnedBy(_note with { NeedsProblemDetails = needsProblemDetails, ManagementApiOnly = managementApiOnly })
            .ShouldBe(expected);

    [Fact]
    public void The_page_explains_the_uri_mapping() =>
        ProblemTypesGenerator.Render([], new Dictionary<string, ProblemTypeNote>(), _ => string.Empty).Content
            .ShouldContain("The slug after the last `/` is the anchor on this page: `https://alvo.dev/errors/forbidden` is [`#forbidden`](#forbidden).");

    [Fact]
    public void A_slug_without_notes_is_refused() =>
        Should.Throw<InvalidOperationException>(() => ProblemTypesGenerator.Render(
            [new ProblemType("validation", "https://alvo.dev/errors/validation", 422, "Refused.")], new Dictionary<string, ProblemTypeNote>(), _ => string.Empty));

    [Fact]
    public void Every_real_slug_has_a_status_and_a_meaning()
    {
        var catalog = ProblemTypeCatalog.Read(XmlDocs.Load([typeof(AlvoProblemTypes).Assembly]));

        catalog.Select(p => p.Slug).ShouldBe(AlvoProblemTypes.All);
        catalog.ShouldAllBe(p => p.Status != null && p.Meaning.Length > 0);
        catalog.ShouldAllBe(p => SlugShape().IsMatch(p.Slug));
    }

    [GeneratedRegex("^[a-z]+(-[a-z]+)*$")]
    private static partial Regex SlugShape();
}
