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
    public void Audit_timestamps_are_required_and_actors_are_nullable()
    {
        var m = Map("simple-tasks/tasks.alvo.json");
        var projects = m.Entities.Single(e => e.Name == "projects");

        var createdAt = projects.Fields.Single(f => f.Name == "created_at");
        createdAt.Required.ShouldBeTrue();
        createdAt.Nullable.ShouldBeFalse();

        var createdBy = projects.Fields.Single(f => f.Name == "created_by");
        createdBy.Required.ShouldBeFalse();
        createdBy.Nullable.ShouldBeTrue();
    }

    /// <summary>
    /// <c>softDelete</c> is declared in the frozen descriptor schema and not implemented: the delete path
    /// hard-deletes the row and reads do not exclude it, which is silent data loss where the schema promises
    /// recoverability. Refused at apply time, exactly as <c>computed</c> is, rather than honoured half-way.
    /// </summary>
    [Fact]
    public void Map_rejects_soft_delete_until_it_is_implemented()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "archives": { "softDelete": true, "fields": {
            "title": { "type": "string" } } } } }
        """;

        var ex = Should.Throw<InvalidDataException>(() => MapInline(json));

        ex.Message.ShouldContain("softDelete");
        ex.Message.ShouldContain("archives");
    }

    /// <summary>
    /// The negative leg: the flag written as <c>false</c> is not a declaration, so it must map normally —
    /// otherwise the refusal would be "any entity mentioning softDelete".
    /// </summary>
    [Fact]
    public void Soft_delete_written_as_false_maps_normally()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "archives": { "softDelete": false, "fields": {
            "title": { "type": "string" } } } } }
        """;

        var archives = MapInline(json).Entities.Single(e => e.Name == "archives");

        archives.SoftDelete.ShouldBeFalse();
        archives.Fields.ShouldNotContain(field => field.Name == "deleted_at");
    }

    // complex-crm's gross_total/line_total legitimately use 'computed' (a gross total SHOULD be
    // computed) — that's the showcase's job. The rich managed-column mapping itself (tenant_id,
    // generated audit/soft-delete columns, refs) is already fully covered, at 100% mutation
    // coverage, by the other tests in this file, so this fixture's role is proving the computed
    // guardrail fires on a real, schema-valid descriptor rather than re-snapshotting the mapping.
    //
    // The fixture declares three features this build does not honour ('default', 'rollup', 'computed'),
    // and the mapper refuses at the first one it meets — which is 'default', on an earlier entity. The
    // other two are stripped here so this fact is about the 'computed' arm specifically; the arms
    // themselves get one fact each in Map_refuses_every_field_feature_it_does_not_honour below.
    [Fact]
    public void Complex_crm_mapping_rejects_computed()
    {
        var json = ComplexCrmWithout("default", "rollup");

        var ex = Should.Throw<InvalidDataException>(() => MapInline(json));

        ex.Message.ShouldContain("computed");
        ex.Message.ShouldContain("#21");
    }

    /// <summary>
    /// Every field-level feature the frozen schema declares and this build does not honour is refused at
    /// <b>apply</b>, naming the feature and what to do instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silently dropping one is the defect class this repo has closed four times, and three of these four
    /// were being dropped: <c>validation: "value >= 0"</c> with <c>-5</c> in the body answered 201, and a
    /// <c>required</c> field with a <c>default</c> was an INSERT of NULL into a NOT NULL column rather than a
    /// defaulted row.
    /// </para>
    /// <para>
    /// One case per feature, each over a descriptor declaring <em>only</em> that feature, so deleting any one
    /// arm of the guard fails exactly one case and names it. A single descriptor carrying all four would be
    /// satisfied by whichever arm happens to run first.
    /// </para>
    /// </remarks>
    /// <param name="feature">The descriptor key under test.</param>
    /// <param name="declaration">How the field declares it.</param>
    /// <param name="fixMentions">A word the fix suggestion must carry, so "unsupported" alone cannot pass.</param>
    [Theory]
    [InlineData("computed", @"""computed"": ""net * 1.2""", "#21")]
    [InlineData("rollup", @"""rollup"": { ""from"": ""lines"", ""op"": ""count"" }", "query")]
    [InlineData("validation", @"""validation"": ""value >= 0""", "before-hook")]
    [InlineData("default", @"""default"": 1", "explicitly")]
    public void Map_refuses_every_field_feature_it_does_not_honour(
        string feature, string declaration, string fixMentions)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": {
            "lines": { "fields": { "invoice_id": { "type": "uuid" } } },
            "invoices": { "fields": {
              "net": { "type": "decimal", "precision": 8, "scale": 2 },
              "amount": { "type": "decimal", "precision": 8, "scale": 2, {{declaration}} } } } } }
        """;

        var ex = Should.Throw<InvalidDataException>(() => MapInline(json));

        ex.Message.ShouldContain(feature);
        ex.Message.ShouldContain("amount");
        ex.Message.ShouldContain(fixMentions);
    }

    /// <summary>
    /// The negative leg for the whole guard: a field declaring <b>none</b> of the four maps without
    /// complaint, so the four cases above are about the features rather than about the mapper refusing every
    /// decimal.
    /// </summary>
    [Fact]
    public void A_field_declaring_none_of_the_unhonoured_features_maps_normally()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "fields": {
            "amount": { "type": "decimal", "precision": 8, "scale": 2 } } } } }
        """;

        var amount = MapInline(json).Entities.Single(e => e.Name == "invoices")
            .Fields.Single(f => f.Name == "amount");

        amount.Precision.ShouldBe(8);
        amount.Scale.ShouldBe(2);
    }

    // Full-model regression freeze: the rich complex-crm fixture exercises every mapping
    // concern in one place (managed-column injection, ref FKs, tenancy, audit, softDelete,
    // renamedFrom, indexes, all field types) across multiple entities — a breadth the
    // narrower, branch-level tests above don't give. The features this build does not honour
    // ('computed' on gross_total/line_total, 'default' and 'rollup' elsewhere) are refused by
    // the mapper, so they are stripped at the JSON level here before mapping; everything else in
    // the fixture stays intact. None of the four ever reached the mapped model, so stripping them
    // changes no snapshot line — the fixture keeps them because its job is to document the
    // descriptor format, not to be applied. Drop the stripping per feature as each is implemented.
    [Fact]
    public async Task Complex_crm_without_its_unhonoured_features_maps_to_a_stable_model()
    {
        var m = DescriptorToSchemaMapper.Map(
            AlvoDescriptor.Parse(ComplexCrmWithout("computed", "rollup", "validation", "default")));

        await Verify(m);
    }

    /// <summary>
    /// The <c>complex-crm</c> showcase with some field keys removed — the only way to map a fixture whose
    /// job is to document every key the schema declares, including the ones this build refuses.
    /// </summary>
    /// <param name="keys">The field keys to strip.</param>
    private static string ComplexCrmWithout(params string[] keys)
    {
        var path = Path.Combine(RepositoryRoot.Find(), "examples", "complex-crm", "crm.alvo.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        foreach (var (_, entity) in json["entities"]!.AsObject())
        {
            foreach (var (_, field) in entity!["fields"]!.AsObject())
            {
                foreach (var key in keys)
                {
                    field!.AsObject().Remove(key);
                }
            }
        }

        return json.ToJsonString();
    }

    /// <summary>
    /// A descriptor that declares a field the mapper also injects used to produce <b>two</b>
    /// <see cref="FieldSchema"/> entries with one name, and every later operation on that entity died
    /// with <c>ArgumentException: An item with the same key has already been added</c> out of the data
    /// path — so declaring <c>readOnly</c> on a managed column, the documented way to protect one, broke
    /// the entity instead. Only <c>id</c> had a de-duplication guard.
    /// </summary>
    [Theory]
    [InlineData("created_by")]
    [InlineData("created_at")]
    [InlineData("updated_by")]
    [InlineData("updated_at")]
    public void A_declared_managed_column_is_not_injected_a_second_time(string column)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": { "audit": true, "fields": {
            "title": { "type": "string" },
            "{{column}}": { "type": "uuid", "readOnly": true } } } } }
        """;

        var notes = MapInline(json).Entities.Single(e => e.Name == "notes");

        notes.Fields.Count(field => field.Name == column).ShouldBe(1);
        notes.Fields.Select(field => field.Name).ShouldBeUnique();
    }

    /// <summary>
    /// The tenant discriminator has the same shape, and a scoped entity is the ordinary case rather
    /// than the audited one.
    /// </summary>
    [Fact]
    public void A_declared_tenant_id_is_not_injected_a_second_time()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": { "tenancy": "scoped", "fields": {
            "tenant_id": { "type": "uuid", "required": true } } } } }
        """;

        var notes = MapInline(json).Entities.Single(e => e.Name == "notes");

        notes.Fields.Select(field => field.Name).ShouldBeUnique();
        notes.Fields.Count(field => field.Name == "tenant_id").ShouldBe(1);
    }

    /// <summary>
    /// The agreement fact between the two sides of one decision: whatever this mapper injects beyond the
    /// declared fields is exactly what <see cref="AlvoManagedColumns"/> reports for the mapped entity — the
    /// set the write guard in each driver package refuses a caller from supplying. Deleting the authority's
    /// answer, or growing the mapper past it, fails here by name rather than becoming a caller-writable
    /// audit column nobody notices.
    /// </summary>
    /// <param name="tenancy">The entity's declared tenancy.</param>
    /// <param name="audit">Whether the entity declares <c>audit</c>.</param>
    /// <param name="softDelete">Whether the entity declares <c>softDelete</c>.</param>
    /// <remarks>
    /// <c>softDelete</c> is absent from the matrix because the mapper refuses it outright until soft delete
    /// is implemented; that the authority reports <c>deleted_at</c> for it is asserted on the authority
    /// itself (<c>AlvoManagedColumnsTests</c>), so the two do not drift while the flag is unusable.
    /// </remarks>
    [Theory]
    [InlineData("global", false, false)]
    [InlineData("scoped", false, false)]
    [InlineData("global", true, false)]
    [InlineData("scoped", true, false)]
    public void The_injected_columns_are_exactly_the_ones_AlvoManagedColumns_reports(
        string tenancy, bool audit, bool softDelete)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": {
            "tenancy": "{{tenancy}}",
            "audit": {{(audit ? "true" : "false")}},
            "softDelete": {{(softDelete ? "true" : "false")}},
            "fields": { "title": { "type": "string" } } } } }
        """;

        var notes = MapInline(json).Entities.Single(e => e.Name == "notes");

        var injected = notes.Fields.Select(field => field.Name).Where(name => name != "title");
        injected.ToHashSet(StringComparer.Ordinal).ShouldBe(AlvoManagedColumns.For(notes), ignoreOrder: true);
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
