using MMLib.Alvo.Analyzers.Generator;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>Which fields get a typed property, which a caller may write, and that the model is a value — the cache's precondition.</summary>
public sealed class DescriptorModelTests
{
    [Fact]
    public void Hidden_excluded_and_json_fields_get_no_property()
    {
        var entity = Read("""
            { "tickets": {
                "events": { "exclude": ["body"] },
                "fields": {
                  "title": { "type": "string", "required": true },
                  "secret": { "type": "string", "hidden": true },
                  "internal_note": { "type": "string", "hidden": "!('admin' in @user.roles)" },
                  "shown": { "type": "string", "hidden": false },
                  "body": { "type": "text" },
                  "payload": { "type": "json" } } } }
            """);

        entity.Untyped.ShouldBe(["secret", "internal_note", "body", "payload"]);
        entity.Fields.Select(f => f.Name).ShouldBe(["id", "title", "shown"]);
    }

    [Fact]
    public void Maintained_and_read_only_fields_are_not_caller_writable()
    {
        var entity = Read("""
            { "orders": { "fields": {
                "note": { "type": "string" },
                "total": { "type": "decimal", "precision": 9, "scale": 2, "computed": "1 + 1" },
                "line_count": { "type": "integer", "rollup": { "from": "lines", "op": "count" } },
                "number": { "type": "string", "sequence": { "format": "{n}" } },
                "enriched": { "type": "string", "virtual": true },
                "locked": { "type": "string", "readOnly": true },
                "sometimes_locked": { "type": "string", "readOnly": "new.note == 'x'" },
                "unlocked": { "type": "string", "readOnly": false } } } }
            """);

        entity.Fields.Where(f => f.CallerWritable).Select(f => f.Name).ShouldBe(["note", "sometimes_locked", "unlocked"]);
    }

    [Fact]
    public void A_field_carries_its_description()
    {
        var entity = Read("{ \"x\": { \"description\": \"An x.\", \"fields\": { \"f\": { \"type\": \"string\", \"description\": \"An f.\" } } } }");

        entity.Description.ShouldBe("An x.");
        entity.Fields[1].Description.ShouldBe("An f.");
        entity.Fields[0].Description.ShouldBeNull();
    }

    [Fact]
    public void The_model_is_value_equal_across_two_reads()
    {
        var first = DescriptorReader.Read("help-desk.alvo.json", Descriptors.HelpDesk, "HelpDesk").Model;
        var second = DescriptorReader.Read("help-desk.alvo.json", Descriptors.HelpDesk, "HelpDesk").Model;

        second.ShouldBe(first);
        second!.GetHashCode().ShouldBe(first!.GetHashCode());
        ReferenceEquals(first, second).ShouldBeFalse();
    }

    [Fact]
    public void A_changed_field_changes_the_model()
    {
        var first = DescriptorReader.Read("help-desk.alvo.json", Descriptors.HelpDesk, "HelpDesk").Model;
        var second = DescriptorReader.Read("help-desk.alvo.json", Descriptors.HelpDesk.Replace("\"maxLength\": 120", "\"maxLength\": 120, \"nullable\": true", StringComparison.Ordinal), "HelpDesk").Model;

        second.ShouldNotBe(first);
    }

    private static EntityModel Read(string entities)
    {
        var result = DescriptorReader.Read("x.alvo.json", Descriptors.With(entities), "X");
        result.Error.ShouldBeNull();

        return result.Model.ShouldNotBeNull().Entities.Single();
    }
}
