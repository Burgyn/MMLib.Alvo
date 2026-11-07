using MMLib.Alvo.Analyzers.Generator;
using System.Text.Json;

namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>What a descriptor — or a Management API snapshot of one — reads as, and what is refused with a reason.</summary>
public sealed class DescriptorReaderTests
{
    private const string Namespace = "HelpDesk";

    [Fact]
    public void A_management_snapshot_reads_as_the_descriptor_it_carries()
    {
        var descriptor = Descriptors.HelpDesk;
        var snapshot = JsonSerializer.Serialize(new { project = "help-desk", revision = 7, descriptorJson = descriptor });

        var fromDescriptor = Read(descriptor);
        var fromSnapshot = Read(snapshot);

        fromSnapshot.ShouldBe(fromDescriptor);
    }

    [Fact]
    public void A_snapshot_whose_descriptor_is_malformed_is_refused_with_its_position()
    {
        var result = DescriptorReader.Read("snapshot.json", "{\"descriptorJson\": \"{\\n ,\"}", Namespace);

        result.Model.ShouldBeNull();
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("descriptorJson");
        error.ShouldContain("line 2, column 2");
        error.ShouldContain("inside that string");
        (result.Line, result.Column).ShouldBe((0, 0));
    }

    [Fact]
    public void Malformed_json_is_refused_with_line_and_column()
    {
        var result = DescriptorReader.Read("bad.alvo.json", "{\n  \"a\": ,\n}", Namespace);

        result.Model.ShouldBeNull();
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("line 2, column 8");
        (result.Line, result.Column).ShouldBe((2, 8));
        result.File.ShouldBe("bad.alvo.json");
    }

    [Fact]
    public void A_root_that_is_not_an_object_is_refused_with_a_reason()
    {
        var result = DescriptorReader.Read("list.alvo.json", "[]", Namespace);

        result.Model.ShouldBeNull();
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("object");
    }

    [Fact]
    public void A_document_without_entities_is_refused_with_a_reason()
    {
        var result = DescriptorReader.Read("empty.alvo.json", "{\"apiVersion\": \"alvo.dev/v1\", \"name\": \"x\"}", Namespace);

        result.Model.ShouldBeNull();
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("'entities'");
    }

    [Theory]
    [InlineData("\"apiVersion\": \"alvo.dev/v2\",")]
    [InlineData("")]
    public void A_foreign_api_version_is_refused_with_a_reason(string apiVersion)
    {
        var result = DescriptorReader.Read("v2.alvo.json", $"{{{apiVersion} \"name\": \"x\", \"entities\": {{}}}}", Namespace);

        result.Model.ShouldBeNull();
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("alvo.dev/v1");
    }

    [Fact]
    public void The_model_carries_the_file_name_project_and_namespace()
    {
        var model = DescriptorReader.Read("/repo/examples/help-desk/help-desk.alvo.json", Descriptors.HelpDesk, Namespace).Model!;

        model.File.ShouldBe("help-desk.alvo.json");
        model.Project.ShouldBe("help-desk");
        model.Namespace.ShouldBe(Namespace);
        model.Entities.Select(e => e.Name).ShouldBe(["tickets"]);
    }

    [Fact]
    public void Roles_list_the_built_ins_then_the_declared_ones_once()
    {
        var model = Read(Descriptors.With(entities: "{}", extra: "\"auth\": { \"roles\": [\"agent\", \"admin\", \"agent\", \"auditor\"] },"));

        model.Roles.ShouldBe(["anon", "authenticated", "admin", "agent", "auditor"]);
    }

    [Fact]
    public void Scoped_tenancy_adds_tenant_id_and_audit_adds_the_four_columns()
    {
        var model = Read(Descriptors.With(
            entities: "{ \"jobs\": { \"audit\": true, \"fields\": { \"title\": { \"type\": \"string\", \"required\": true } } },"
                + " \"codes\": { \"tenancy\": \"global\", \"fields\": { \"code\": { \"type\": \"string\" } } } }",
            extra: "\"tenancy\": { \"enabled\": true },"));

        var jobs = model.Entities[0].Fields;
        jobs.Select(f => f.Name).ShouldBe(["id", "tenant_id", "created_at", "created_by", "updated_at", "updated_by", "title"]);
        jobs.Select(f => f.Kind).ShouldBe([FieldKind.Uuid, FieldKind.Uuid, FieldKind.DateTime, FieldKind.Uuid, FieldKind.DateTime, FieldKind.Uuid, FieldKind.String]);
        jobs.Select(f => f.Nullable).ShouldBe([false, false, false, true, false, true, false]);
        jobs.Take(6).ShouldAllBe(f => !f.CallerWritable);
        model.Entities[1].Fields.Select(f => f.Name).ShouldBe(["id", "code"]);
    }

    [Fact]
    public void An_entity_declaring_scoped_tenancy_is_scoped_without_the_project_switch()
    {
        var model = Read(Descriptors.With(entities: "{ \"jobs\": { \"tenancy\": \"scoped\", \"fields\": {} } }"));

        model.Entities[0].Fields.Select(f => f.Name).ShouldBe(["id", "tenant_id"]);
    }

    [Fact]
    public void Field_types_map_to_their_kinds_and_ref_to_a_uuid()
    {
        var model = Read(Descriptors.With(entities: """
            { "all": { "fields": {
                "s": { "type": "string" }, "t": { "type": "text" }, "i": { "type": "integer" }, "d": { "type": "decimal", "precision": 5, "scale": 1 },
                "b": { "type": "boolean" }, "day": { "type": "date" }, "at": { "type": "datetime" }, "u": { "type": "uuid" },
                "e": { "type": "enum", "values": ["a", "b"] }, "r": { "type": "ref", "entity": "all" } } } }
            """));

        model.Entities[0].Fields.Skip(1).Select(f => f.Kind).ShouldBe([
            FieldKind.String, FieldKind.String, FieldKind.Integer, FieldKind.Decimal, FieldKind.Boolean, FieldKind.Date,
            FieldKind.DateTime, FieldKind.Uuid, FieldKind.Enum, FieldKind.Uuid]);
        model.Entities[0].Fields[9].EnumValues.Select(v => v.Value).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Nullability_follows_the_mapper_and_virtual_is_always_nullable()
    {
        var model = Read(Descriptors.With(entities: """
            { "n": { "fields": {
                "plain": { "type": "string" }, "required": { "type": "string", "required": true },
                "required_but_nullable": { "type": "string", "required": true, "nullable": true },
                "optional_not_null": { "type": "string", "nullable": false, "default": "x" },
                "enriched": { "type": "string", "required": true, "virtual": true } } } }
            """));

        var fields = model.Entities[0].Fields.Skip(1).ToList();
        fields.Select(f => f.Nullable).ShouldBe([true, false, true, false, true]);
        fields.Select(f => f.Required).ShouldBe([false, true, true, false, true]);
        fields.Select(f => f.HasDefault).ShouldBe([false, false, false, true, false]);
    }

    [Fact]
    public void Unknown_keys_and_ignored_blocks_do_not_disturb_the_read()
    {
        var model = Read(Descriptors.With(
            entities: "{ \"x\": { \"future\": [1, {\"a\": null}], \"stateMachine\": {}, \"fields\": { \"f\": { \"type\": \"string\", \"surprise\": true } } } }",
            extra: "\"automation\": { \"r\": {} }, \"dynamicEntities\": { \"enabled\": true }, \"functions\": { \"f\": { \"input\": {} } },"));

        model.Entities.Single().Fields.Select(f => f.Name).ShouldBe(["id", "f"]);
    }

    [Fact]
    public void A_dynamic_entity_is_read_like_a_physical_one()
    {
        var model = Read(Descriptors.With(entities: "{ \"notes\": { \"storage\": \"dynamic\", \"fields\": { \"text\": { \"type\": \"text\" } } } }"));

        model.Entities.Single().Fields.Select(f => f.Name).ShouldBe(["id", "text"]);
    }

    private static DescriptorModel Read(string text)
    {
        var result = DescriptorReader.Read("help-desk.alvo.json", text, Namespace);
        result.Error.ShouldBeNull();

        return result.Model.ShouldNotBeNull();
    }
}
