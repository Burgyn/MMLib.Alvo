using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The dashboard's copy of the frozen name patterns is the frozen name patterns.
/// </summary>
/// <remarks>
/// <c>schema/project.schema.json</c> is a repository file rather than a shipped asset, so the
/// dashboard cannot read it at runtime and carries the three patterns as constants. This is what
/// stops that copy from becoming a second opinion: it reads the schema and compares. A change to
/// the schema fails here, in one place, instead of producing a control that offers a name the
/// apply refuses.
/// </remarks>
public sealed class DescriptorNameTests
{
    private static readonly JsonDocument _schema = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")));

    [Fact]
    public void The_entity_name_pattern_is_the_schemas_own()
        => Pattern("properties", "entities").ShouldBe(DescriptorNames.Member);

    [Fact]
    public void The_field_name_pattern_is_the_schemas_own()
        => Pattern("$defs", "entity", "properties", "fields").ShouldBe(DescriptorNames.Member);

    [Fact]
    public void The_endpoint_name_pattern_is_the_schemas_own()
        => Pattern("properties", "webhooks", "properties", "endpoints").ShouldBe(DescriptorNames.Identifier);

    [Fact]
    public void The_project_name_pattern_is_the_schemas_own()
        => _schema.RootElement
            .GetProperty("properties").GetProperty("name").GetProperty("pattern").GetString()
            .ShouldBe(DescriptorNames.Project);

    /// <summary>
    /// None of the patterns admits a character that means something in HTML.
    /// </summary>
    /// <remarks>
    /// This is the half of the Import boundary that is about rendering rather than about the apply:
    /// every screen builds markup from names, so a name the schema admits must be inert as text.
    /// The assertion is over the patterns themselves, so it keeps holding as they change.
    /// </remarks>
    [Theory]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("&")]
    public void No_name_pattern_admits_a_character_that_means_something_in_markup(string dangerous)
    {
        DescriptorNames.IsMember($"a{dangerous}b").ShouldBeFalse();
        DescriptorNames.IsIdentifier($"a{dangerous}b").ShouldBeFalse();
    }

    [Fact]
    public void A_descriptor_whose_entity_name_the_schema_refuses_is_refused_here()
        => DescriptorNames.FirstRefusal(
            """{"name":"borrowed","entities":{"Things":{"fields":{"a":{"type":"string"}}}}}""")
            .ShouldNotBeNull();

    [Fact]
    public void A_descriptor_the_schema_admits_is_admitted_here()
        => DescriptorNames.FirstRefusal(
            """{"name":"borrowed","entities":{"things":{"fields":{"a":{"type":"string"}}}}}""")
            .ShouldBeNull();

    [Fact]
    public void Text_that_is_not_json_is_a_sentence_rather_than_a_throw()
        => DescriptorNames.FirstRefusal("not json at all").ShouldNotBeNull();

    private static string? Pattern(params string[] path)
    {
        var element = _schema.RootElement;
        foreach (var segment in path)
        {
            element = element.GetProperty(segment);
        }

        return element.GetProperty("propertyNames").GetProperty("pattern").GetString();
    }
}
