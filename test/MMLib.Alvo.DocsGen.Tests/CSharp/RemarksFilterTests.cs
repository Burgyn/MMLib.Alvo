using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Xml;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Tests.CSharp;

public class RemarksFilterTests
{
    private static readonly HashSet<string> _internal = new(StringComparer.Ordinal) { "AlvoIdentitySchema" };

    [Theory]
    [InlineData("As spec §X.1 sketches, this binds once.")]
    [InlineData("Issue #233 owns the vocabulary.")]
    [InlineData("Review on the PR rejected the other shape.")]
    [InlineData("Ruling V settles the status.")]
    [InlineData("See `docs/architecture/host.md` for the history.")]
    [InlineData("The plan 2026-10-09-f6-docs-site.md records it.")]
    [InlineData("Mirrors `AlvoIdentitySchema.Users` on purpose.")]
    public void A_paragraph_citing_contributor_material_is_dropped(string paragraph) =>
        RemarksFilter.ForReaders(paragraph, _internal).ShouldBeEmpty();

    [Theory]
    [InlineData("**Keyed, and that is a security decision.** The header path cannot resolve it.")]
    [InlineData("Use `IAlvoContextResolver` for keys; `C#` and `&#123;` are not references.")]
    public void A_paragraph_for_host_authors_is_kept(string paragraph) =>
        RemarksFilter.ForReaders(paragraph, _internal).ShouldBe(paragraph);

    [Fact]
    public void Only_the_offending_paragraph_goes_and_code_blocks_stay_whole()
    {
        var remarks = "Kept first.\n\nDropped per #12.\n\n```csharp\nvar a = 1;\n\nvar b = 2;\n```\n\nKept last.";

        RemarksFilter.ForReaders(remarks, _internal).ShouldBe("Kept first.\n\n```csharp\nvar a = 1;\n\nvar b = 2;\n```\n\nKept last.");
    }

    [Fact]
    public void Internal_type_names_come_from_internal_namespaces()
    {
        var names = RemarksFilter.InternalTypeNames(ShippedAssemblies.All);

        names.ShouldContain("AlvoIdentitySchema");
        names.ShouldNotContain("AlvoIdentity");
    }

    [Fact]
    public void No_rendered_remarks_cite_a_spec_section_or_an_issue()
    {
        var docs = XmlDocs.Load(ShippedPackages.All.Select(p => p.Assembly));
        var ids = ShippedPackages.All
            .SelectMany(p => ApiScope.TypesOf(p.Assembly))
            .SelectMany(type => ApiScope.MembersOf(type).Select(ApiScope.DocIdOf).Prepend(DocId.Of(type)));

        var leaks = ids
            .Select(id => (id, Text: RemarksFilter.ForReaders(docs.Remarks(id))))
            .Where(remarks => remarks.Text.Contains('§', StringComparison.Ordinal) || Regex.IsMatch(remarks.Text, @"(?<![&\w])#\d+"))
            .Select(remarks => remarks.id)
            .ToList();

        leaks.ShouldBeEmpty();
    }
}
