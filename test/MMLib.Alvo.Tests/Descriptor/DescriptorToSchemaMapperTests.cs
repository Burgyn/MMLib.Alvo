using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;
using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Descriptor;

public class DescriptorToSchemaMapperTests
{
    private static SchemaModel Map(string file)
        => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", file))));

    [Fact]
    public void Injects_id_when_absent()
    {
        var m = Map("simple-tasks/tasks.alvo.json");
        var tasks = m.Entities.Single(e => e.Name == "tasks");
        tasks.Fields.ShouldContain(f => f.Name == "id" && f.Type == FieldType.Uuid);
    }

    [Fact]
    public void Audit_entity_gets_managed_audit_columns()
    {
        var m = Map("simple-tasks/tasks.alvo.json");
        var tasks = m.Entities.Single(e => e.Name == "tasks");
        // tasks in simple-tasks declares audit:true
        tasks.Fields.Select(f => f.Name).ShouldContain("created_at");
        tasks.Fields.Select(f => f.Name).ShouldContain("updated_by");
    }

    [Fact]
    public void Soft_delete_column_is_nullable_and_audit_timestamps_are_required()
    {
        // simple-tasks' "projects" entity declares both audit:true and softDelete:true.
        var m = Map("simple-tasks/tasks.alvo.json");
        var projects = m.Entities.Single(e => e.Name == "projects");

        var createdAt = projects.Fields.Single(f => f.Name == "created_at");
        createdAt.Required.ShouldBeTrue();
        createdAt.Nullable.ShouldBeFalse();

        var createdBy = projects.Fields.Single(f => f.Name == "created_by");
        createdBy.Required.ShouldBeFalse();
        createdBy.Nullable.ShouldBeTrue();

        var deletedAt = projects.Fields.Single(f => f.Name == "deleted_at");
        deletedAt.Required.ShouldBeFalse();
        deletedAt.Nullable.ShouldBeTrue();
    }

    // complex-crm's gross_total/line_total legitimately use 'computed' (a gross total SHOULD be
    // computed) — that's the showcase's job. The rich managed-column mapping itself (tenant_id,
    // generated audit/soft-delete columns, refs) is already fully covered, at 100% mutation
    // coverage, by the other tests in this file, so this fixture's role is proving the computed
    // guardrail fires on a real, schema-valid descriptor rather than re-snapshotting the mapping.
    [Fact]
    public void Complex_crm_mapping_rejects_computed()
    {
        var ex = Should.Throw<InvalidDataException>(() => Map("complex-crm/crm.alvo.json"));

        ex.Message.ShouldContain("computed");
        ex.Message.ShouldContain("#21");
    }

    // Full-model regression freeze: the rich complex-crm fixture exercises every mapping
    // concern in one place (managed-column injection, ref FKs, tenancy, audit, softDelete,
    // renamedFrom, indexes, all field types) across multiple entities — a breadth the
    // narrower, branch-level tests above don't give. 'computed' (gross_total/line_total) is
    // rejected by the mapper until #21 (CEL→SQL compiler), so it is stripped at the JSON level
    // here — the one not-yet-supported feature — before mapping; everything else in the
    // fixture stays intact. Drop the stripping and snapshot the descriptor directly once #21 lands.
    [Fact]
    public async Task Complex_crm_without_computed_maps_to_a_stable_model()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "examples", "complex-crm", "crm.alvo.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        foreach (var (_, entity) in json["entities"]!.AsObject())
        {
            foreach (var (_, field) in entity!["fields"]!.AsObject())
            {
                field!.AsObject().Remove("computed");
            }
        }

        var descriptor = AlvoDescriptor.Parse(json.ToJsonString());
        var m = DescriptorToSchemaMapper.Map(descriptor);

        await Verify(m);
    }

    private const string WithComputed = """
    {
      "apiVersion": "alvo.dev/v1",
      "name": "demo",
      "entities": {
        "invoices": {
          "fields": {
            "net": { "type": "decimal" },
            "gross": { "type": "decimal", "computed": "net * 1.2" }
          }
        }
      }
    }
    """;

    [Fact]
    public void Map_rejects_computed_until_cel_compiler()
    {
        var descriptor = AlvoDescriptor.Parse(WithComputed);

        var ex = Should.Throw<InvalidDataException>(() => DescriptorToSchemaMapper.Map(descriptor));

        ex.Message.ShouldContain("computed");
        ex.Message.ShouldContain("#21");
    }

    private static SchemaModel MapInline(string descriptorJson)
        => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(descriptorJson));

    private static RefSchema RefOf(SchemaModel model, string entity = "orders", string field = "target")
        => model.Entities.Single(e => e.Name == entity).Fields.Single(f => f.Name == field).Reference
            ?? throw new InvalidOperationException("Field has no reference.");

    [Theory]
    [InlineData("cascade", OnDelete.Cascade)]
    [InlineData("setNull", OnDelete.SetNull)]
    [InlineData("restrict", OnDelete.Restrict)]
    public void MapOnDelete_maps_every_explicit_action(string onDelete, OnDelete expected)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": {
            "targets": { "fields": { "name": { "type": "string" } } },
            "orders": { "fields": {
              "target": { "type": "ref", "entity": "targets", "onDelete": "{{onDelete}}" } } } } }
        """;

        RefOf(MapInline(json)).OnDelete.ShouldBe(expected);
    }

    [Fact]
    public void MapOnDelete_defaults_to_restrict_when_absent()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": {
            "targets": { "fields": { "name": { "type": "string" } } },
            "orders": { "fields": {
              "target": { "type": "ref", "entity": "targets" } } } } }
        """;

        RefOf(MapInline(json)).OnDelete.ShouldBe(OnDelete.Restrict);
    }

    [Theory]
    [InlineData("string", FieldType.String)]
    [InlineData("text", FieldType.Text)]
    [InlineData("integer", FieldType.Integer)]
    [InlineData("boolean", FieldType.Boolean)]
    [InlineData("date", FieldType.Date)]
    [InlineData("datetime", FieldType.DateTime)]
    [InlineData("uuid", FieldType.Uuid)]
    [InlineData("json", FieldType.Json)]
    public void MapType_maps_every_simple_field_type(string descriptorType, FieldType expected)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "widgets": { "fields": { "value": { "type": "{{descriptorType}}" } } } } }
        """;

        var field = MapInline(json).Entities.Single(e => e.Name == "widgets").Fields.Single(f => f.Name == "value");
        field.Type.ShouldBe(expected);
    }

    [Fact]
    public void MapType_maps_enum_and_carries_its_values()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "tasks": { "fields": {
            "priority": { "type": "enum", "values": ["low", "high"] } } } } }
        """;

        var field = MapInline(json).Entities.Single(e => e.Name == "tasks").Fields.Single(f => f.Name == "priority");
        field.Type.ShouldBe(FieldType.Enum);
        field.EnumValues.ShouldBe(["low", "high"]);
    }

    [Fact]
    public void ResolveTenancy_entity_scoped_override_applies_even_when_project_tenancy_is_disabled()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { "tenancy": "scoped", "fields": { "name": { "type": "string" } } } } }
        """;

        var orders = MapInline(json).Entities.Single(e => e.Name == "orders");

        orders.Tenancy.ShouldBe(TenancyMode.Scoped);
        orders.Fields.ShouldContain(f => f.Name == "tenant_id");
    }

    [Fact]
    public void ResolveTenancy_entity_global_override_applies_even_when_project_tenancy_is_enabled()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo", "tenancy": { "enabled": true },
          "entities": {
            "countries": { "tenancy": "global", "fields": { "name": { "type": "string" } } },
            "orders": { "fields": { "name": { "type": "string" } } } } }
        """;

        var model = MapInline(json);

        model.Entities.Single(e => e.Name == "countries").Tenancy.ShouldBe(TenancyMode.Global);
        model.Entities.Single(e => e.Name == "countries").Fields.ShouldNotContain(f => f.Name == "tenant_id");
        model.Entities.Single(e => e.Name == "orders").Tenancy.ShouldBe(TenancyMode.Scoped);
    }

    [Fact]
    public void ResolveTenancy_is_null_when_neither_project_nor_entity_declares_tenancy()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { "fields": { "name": { "type": "string" } } } } }
        """;

        var orders = MapInline(json).Entities.Single(e => e.Name == "orders");

        orders.Tenancy.ShouldBeNull();
        orders.Fields.ShouldNotContain(f => f.Name == "tenant_id");
    }
}
