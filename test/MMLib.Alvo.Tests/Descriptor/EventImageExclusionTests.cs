using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary><c>entities.&lt;e&gt;.events.exclude</c>: mapped onto the applied schema and refused when it names what it cannot.</summary>
public class EventImageExclusionTests
{
    private readonly DescriptorValidator _validator = new();

    [Fact]
    public void The_exclusions_reach_the_applied_schema()
        => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(Notes("""["salary"]""")))
            .Entities.Single(entity => entity.Name == "notes").EventImageExclusions.ShouldBe(["salary"]);

    [Fact]
    public void An_entity_that_excludes_nothing_carries_an_empty_list()
        => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(Notes(exclude: null)))
            .Entities.Single(entity => entity.Name == "notes").EventImageExclusions.ShouldBeEmpty();

    [Fact]
    public void Excluding_a_declared_field_applies()
        => _validator.Validate(Notes("""["salary"]""")).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("id")]
    [InlineData("tenant_id")]
    public void The_columns_that_key_an_event_cannot_be_excluded(string column)
    {
        var refusal = _validator.Validate(Notes($"""["{column}"]""")).Errors.ShouldHaveSingleItem();

        refusal.Path.ShouldBe("/entities/notes/events/exclude/0");
        refusal.Message.ShouldContain(column);
        refusal.FixSuggestion.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("\"tenancy\": \"scoped\",", "tenant_id")]
    [InlineData("\"audit\": true,", "created_at")]
    public void A_column_the_entitys_traits_manage_is_refused_as_framework_managed(string trait, string column)
    {
        var refusal = _validator.Validate(Notes($"""["{column}"]""").Replace(
                "\"notes\": {", $"\"notes\": {{ {trait}", StringComparison.Ordinal)
            .Replace("\"entities\":", "\"tenancy\": { \"enabled\": true },\n  \"entities\":", StringComparison.Ordinal))
            .Errors.ShouldHaveSingleItem();

        refusal.Path.ShouldBe("/entities/notes/events/exclude/0");
        refusal.Message.ShouldContain($"'{column}' is a framework-managed column");
        refusal.FixSuggestion.ShouldBe($"Remove '{column}' from events.exclude.");
    }

    [Fact]
    public void An_ordinary_field_named_like_an_audit_column_can_be_excluded_on_an_entity_without_audit()
        => _validator.Validate(Notes("""["created_at"]""").Replace(
                "\"salary\": { \"type\": \"integer\" }", "\"salary\": { \"type\": \"integer\" }, \"created_at\": { \"type\": \"datetime\" }",
                StringComparison.Ordinal))
            .IsValid.ShouldBeTrue("created_at is managed only on an entity with audit");

    [Fact]
    public void A_name_no_field_carries_is_refused_at_its_own_pointer()
    {
        var refusal = _validator.Validate(Notes("""["salary", "nope"]""")).Errors.ShouldHaveSingleItem();

        refusal.Path.ShouldBe("/entities/notes/events/exclude/1");
        refusal.Message.ShouldContain("nope");
    }

    private static string Notes(string? exclude) => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "exclusions",
          "entities": {
            "notes": {
              "fields": { "title": { "type": "string" }, "salary": { "type": "integer" } }{{(exclude is null ? string.Empty : $", \"events\": {{ \"exclude\": {exclude} }}")}}
            }
          }
        }
        """;
}
