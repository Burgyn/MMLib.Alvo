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

    [Fact]
    public void An_after_hook_condition_reading_an_excluded_field_is_refused_at_its_pointer()
    {
        var refusal = _validator.Validate(NotesWithHook("""{ "condition": "new.salary > 10", "action": { "type": "webhook", "endpoint": "crm" } }"""))
            .Errors.ShouldHaveSingleItem();

        refusal.Path.ShouldBe("/entities/notes/hooks/afterUpdate/0/condition");
        refusal.Message.ShouldContain("salary");
        refusal.FixSuggestion.ShouldNotBeNull().ShouldContain("events/exclude");
    }

    [Fact]
    public void Changed_still_names_an_excluded_field_so_testing_it_applies()
        => _validator.Validate(NotesWithHook("""{ "condition": "changed(salary)", "action": { "type": "webhook", "endpoint": "crm" } }"""))
            .IsValid.ShouldBeTrue();

    [Fact]
    public void An_email_recipient_template_reading_an_excluded_field_is_refused_at_its_pointer()
    {
        var refusal = _validator.Validate(NotesWithHook(
                """{ "action": { "type": "email", "template": "note-changed", "to": "{{new.salary}}@example.test" } }"""))
            .Errors.ShouldHaveSingleItem();

        refusal.Path.ShouldBe("/entities/notes/hooks/afterUpdate/0/action/to");
        refusal.Message.ShouldContain("salary");
    }

    [Fact]
    public void Every_excluded_field_a_condition_reads_is_refused_on_its_own()
    {
        var refusals = _validator.Validate(NotesWithHook(
                """{ "condition": "new.salary > 10 && bonus > 1 && old.salary < 5", "action": { "type": "webhook", "endpoint": "crm" } }""",
                exclude: """["salary", "bonus"]"""))
            .Errors;

        refusals.Select(refusal => refusal.Path).ShouldAllBe(path => path == "/entities/notes/hooks/afterUpdate/0/condition");
        refusals.Count.ShouldBe(2);
        refusals[0].Message.ShouldContain("'salary'");
        refusals[1].Message.ShouldContain("'bonus'");
    }

    [Fact]
    public void Every_excluded_field_a_payload_template_reads_is_refused_on_its_own()
    {
        var refusals = _validator.Validate(NotesWithHook(
                """{ "action": { "type": "webhook", "endpoint": "crm", "payload": "[\"{{new.salary}}\", \"{{old.bonus}}\", \"{{new.title}}\"]" } }""",
                exclude: """["salary", "bonus"]"""))
            .Errors;

        refusals.Select(refusal => refusal.Path).ShouldAllBe(path => path == "/entities/notes/hooks/afterUpdate/0/action/payload");
        refusals.Count.ShouldBe(2);
        refusals[0].Message.ShouldContain("'salary'");
        refusals[1].Message.ShouldContain("'bonus'");
    }

    [Fact]
    public void A_template_reading_a_field_that_is_not_excluded_applies()
        => _validator.Validate(NotesWithHook(
                """{ "action": { "type": "webhook", "endpoint": "crm", "payload": "[\"{{new.title}}\", \"{{old.title}}\"]" } }"""))
            .IsValid.ShouldBeTrue();

    private static string NotesWithHook(string hook, string exclude = """["salary"]""") => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "exclusions",
          "webhooks": { "endpoints": { "crm": { "url": "https://crm.example.test/hook", "secretRef": "crm-secret" } } },
          "templates": { "note-changed": { "subject": "Changed", "body": "A note changed." } },
          "entities": {
            "notes": {
              "fields": { "title": { "type": "string" }, "salary": { "type": "integer" }, "bonus": { "type": "integer" } },
              "events": { "exclude": {{exclude}} },
              "hooks": { "afterUpdate": [ {{hook}} ] }
            }
          }
        }
        """;

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
