using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
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

    // complex-crm's gross_total/line_total legitimately use 'computed' (a gross total SHOULD be computed) —
    // that's the showcase's job, and since #21 the mapper honours it. The fact therefore inverted: it used to
    // prove the guardrail fired on a real, schema-valid descriptor, and now proves the feature reaches the
    // applied schema from one. 'default' is still unhonoured and is stripped; 'rollup' is stripped only because
    // this showcase's own rollup declares a 'where' filter, which the next fact is about.
    [Fact]
    public void Complex_crm_maps_the_computed_sources_it_declares()
    {
        var model = MapInline(ComplexCrmWithout("default", "rollup"));

        FieldOf(model, "invoice_items", "line_total").ComputedExpression.ShouldBe("unit_price * amount");
        FieldOf(model, "invoices", "gross_total").ComputedExpression.ShouldBe("net_total + vat_total");
    }

    /// <summary>
    /// The showcase's own <c>companies.open_deals</c> rollup filters the child records
    /// (<c>stage in ['lead', 'offer']</c>), and this build does not evaluate that filter — so the whole
    /// declaration is refused rather than silently counting <em>every</em> deal.
    /// </summary>
    /// <remarks>
    /// Asserted against the real showcase rather than a synthetic descriptor, because the value of this refusal
    /// is precisely that a plausible, schema-valid, hand-authored rollup hits it: a count of open deals that
    /// silently became a count of all deals is the failure mode the refusal exists for.
    /// </remarks>
    [Fact]
    public void Complex_crm_rollup_with_a_where_filter_is_refused()
    {
        var ex = Should.Throw<InvalidDataException>(() => MapInline(ComplexCrmWithout("default")));

        ex.Message.ShouldContain("open_deals");
        ex.Message.ShouldContain("aggregates every record");
    }

    private static FieldSchema FieldOf(SchemaModel model, string entity, string field) =>
        model.Entities.Single(candidate => candidate.Name == entity)
            .Fields.Single(candidate => candidate.Name == field);

    /// <summary>
    /// <b>The tie between the two refusal passes.</b> Every entry in
    /// <see cref="UnhonouredFeatures.OnAField"/> — the table <c>DescriptorValidator</c> reports from — is one
    /// the <em>mapper</em> also throws for, driven off that table rather than off a copy of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fact whose absence let the list become four hand-written copies. A theory with its own
    /// <c>[InlineData]</c> per feature proves each arm works and proves nothing about whether the two passes
    /// agree: adding a fifth feature to the validator's table and forgetting the mapper left both green. Driven
    /// off the table, a new entry fails here until the mapper honours it too, and there is nowhere to add a
    /// feature that only one pass refuses.
    /// </para>
    /// <para>
    /// The declaration is synthesised from the table's own path, so the theory needs no per-feature JSON
    /// either — the one thing it cannot derive is a <em>value</em> the schema accepts for each key, which
    /// <see cref="DeclarationFor"/> supplies and which fails loudly for a key it has not been taught.
    /// </para>
    /// </remarks>
    /// <param name="path">The table entry's path.</param>
    [Theory]
    [MemberData(nameof(EveryUnhonouredFieldFeature))]
    public void Map_refuses_every_field_feature_the_table_records(string path)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": {
            "lines": { "fields": { "invoice_id": { "type": "uuid" } } },
            "invoices": { "fields": {
              "net": { "type": "decimal", "precision": 8, "scale": 2 },
              "amount": { "type": "decimal", "precision": 8, "scale": 2, {{DeclarationFor(path)}} } } } } }
        """;

        var ex = Should.Throw<InvalidDataException>(() => MapInline(json));

        ex.Message.ShouldContain(path);
        ex.Message.ShouldContain("amount");
    }

    /// <summary>The same tie one layer up, for the entity-level table — <c>softDelete</c> and the six hook points.</summary>
    /// <param name="path">The table entry's path.</param>
    [Theory]
    [MemberData(nameof(EveryUnhonouredEntityFeature))]
    public void Map_refuses_every_entity_feature_the_table_records(string path)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": {
            {{DeclarationFor(path)}},
            "fields": { "title": { "type": "string" } } } } }
        """;

        var ex = Should.Throw<InvalidDataException>(() => MapInline(json));

        ex.Message.ShouldContain(path);
        ex.Message.ShouldContain("notes");
    }

    public static TheoryData<string> EveryUnhonouredFieldFeature() =>
        [.. UnhonouredFeatures.OnAField.Select(feature => feature.Path)];

    public static TheoryData<string> EveryUnhonouredEntityFeature() =>
        [.. UnhonouredFeatures.OnAnEntity.Select(feature => feature.Path)];

    /// <summary>
    /// A schema-valid declaration of one table entry, for the theories that are driven off the table.
    /// </summary>
    /// <remarks>
    /// It <b>throws</b> for a path it does not know rather than guessing a shape, so adding a table entry
    /// fails the theory with "teach DeclarationFor about it" instead of silently testing a key the schema
    /// would have rejected anyway — which would be a green theory case asserting nothing.
    /// </remarks>
    /// <param name="path">The table entry's path.</param>
    private static string DeclarationFor(string path) => path switch
    {
        "computed" => @"""computed"": ""net * 1.2""",
        "rollup" => @"""rollup"": { ""from"": ""lines"", ""op"": ""count"" }",
        "validation" => @"""validation"": ""value >= 0""",
        "default" => @"""default"": 1",
        "softDelete" => @"""softDelete"": true",
        _ when path.StartsWith("hooks/before", StringComparison.Ordinal) =>
            $@"""hooks"": {{ ""{path["hooks/".Length..]}"": [ {{ ""action"": {{ ""reject"": ""no"" }} }} ] }}",
        // An after-hook's action is polymorphic on a 'type' discriminator (AutomationAction), so a shape
        // without it does not parse at all — which is what the first version of this method got wrong, and
        // what the theory caught by throwing NotSupportedException instead of InvalidDataException.
        _ when path.StartsWith("hooks/after", StringComparison.Ordinal) =>
            $@"""hooks"": {{ ""{path["hooks/".Length..]}"": [ {{ ""action"": {{ ""type"": ""webhook"", ""endpoint"": ""notify"" }} }} ] }}",
        _ => throw new InvalidOperationException(
            $"'{path}' is in UnhonouredFeatures but DeclarationFor does not know how to declare it. Teach it "
            + "a schema-valid declaration, or the theory case would assert nothing."),
    };

    /// <summary>
    /// <b>Every example this repository ships as runnable really maps</b> — no refusal, no exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The apply-time refusals are right, and their fallout was not: three shipped examples declared features
    /// the build does not honour, so a user following the README got a file that would be rejected. Two were
    /// cleaned; <c>complex-crm</c> is deliberately kept rich and carries a
    /// <see cref="AlvoExamples.NotRunnableMarker"/> saying so.
    /// </para>
    /// <para>
    /// Driven off the tree rather than a list of names, so a <em>new</em> example is covered the moment it is
    /// added — nobody has to remember to extend a theory. The non-empty assertion is what stops the whole
    /// thing passing vacuously if the enumeration ever returns nothing (a moved directory, a renamed
    /// extension), which is the failure mode a file-scanning fact has.
    /// </para>
    /// </remarks>
    /// <param name="descriptorPath">One runnable example.</param>
    [Theory]
    [MemberData(nameof(EveryRunnableExample))]
    public void Every_runnable_example_maps_without_refusal(string descriptorPath)
    {
        var descriptor = AlvoDescriptor.Parse(File.ReadAllText(descriptorPath));

        var model = DescriptorToSchemaMapper.Map(descriptor);

        model.Entities.ShouldNotBeEmpty($"'{Path.GetFileName(descriptorPath)}' mapped to no entities at all");
    }

    /// <summary>
    /// <b>And every example marked not runnable really is refused.</b> This is the half that makes the marker
    /// a claim rather than a comment.
    /// </summary>
    /// <remarks>
    /// Without it, nothing would force the marker to shrink: when <c>default</c>, <c>rollup</c>,
    /// <c>computed</c> and hooks eventually land, <c>complex-crm</c> becomes appliable and its
    /// <c>NOT-RUNNABLE.md</c> would quietly go on telling readers to start elsewhere. With it, the last of
    /// those features to land fails this fact until the file is deleted — which is the only kind of marker
    /// worth having.
    /// </remarks>
    /// <param name="descriptorPath">One example marked not runnable.</param>
    [Theory]
    [MemberData(nameof(EveryExampleMarkedNotRunnable))]
    public void Every_example_marked_not_runnable_really_is_refused(string descriptorPath)
    {
        var descriptor = AlvoDescriptor.Parse(File.ReadAllText(descriptorPath));

        Should.Throw<InvalidDataException>(
            () => DescriptorToSchemaMapper.Map(descriptor),
            $"'{Path.GetFileName(descriptorPath)}' carries {AlvoExamples.NotRunnableMarker} but now applies "
            + "cleanly — delete the marker, and the README paragraph that points readers away from it");
    }

    public static TheoryData<string> EveryRunnableExample()
    {
        var runnable = AlvoExamples.Runnable().ToList();
        runnable.ShouldNotBeEmpty("the examples tree must be findable, or this theory covers nothing");
        return [.. runnable];
    }

    public static TheoryData<string> EveryExampleMarkedNotRunnable() => [.. AlvoExamples.NotRunnable()];

    /// <summary>
    /// <b>No shipped example declares an <c>after*</c> hook</b>, which is what makes PR5a's removal of the
    /// three <c>after*</c> entries from <see cref="UnhonouredFeatures"/> safe for the example corpus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hazard is specific and it is created by shrinking that table: <c>complex-crm</c> ships five
    /// expressions that do not compile, four of them inside its <c>hooks</c> block, and all of them are
    /// invisible today because the hook point is refused before anything is compiled. The day a hook entry
    /// leaves, the example's refusal reason silently changes from a structured unhonoured-feature error to a
    /// CEL syntax error — and <see cref="Every_example_marked_not_runnable_really_is_refused"/> keeps passing,
    /// because it asserts only that <em>an</em> <see cref="InvalidDataException"/> was thrown.
    /// </para>
    /// <para>
    /// PR5a is safe from that only because the corpus declares <c>beforeCreate</c> and <c>beforeUpdate</c> and
    /// nothing else, so the example stays refused by the two <c>before*</c> entries that remain. That is a
    /// measured property of the tree rather than an assumption, which is what this fact records; the example's
    /// own five fixes and the strengthening of the refusal-reason assertion belong to the PR that lifts a
    /// <c>before*</c> refusal.
    /// </para>
    /// <para>
    /// Driven off the tree rather than off <c>complex-crm</c> by name, so a <em>new</em> example declaring an
    /// after-hook fails this the moment it is added.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_shipped_example_declares_an_after_hook_so_pr5a_exposes_none_of_their_cel_defects()
    {
        var hooks = AlvoExamples.Descriptors()
            .Select(path => AlvoDescriptor.Parse(File.ReadAllText(path)))
            .SelectMany(descriptor => descriptor.Entities.Values)
            .Select(entity => entity.Hooks)
            .OfType<EntityHooks>()
            .ToList();

        hooks.ShouldNotBeEmpty(
            "at least one example must declare hooks, or this fact passes by covering nothing — it is "
            + "complex-crm today");
        hooks.ShouldAllBe(
            hook => hook.AfterCreate == null && hook.AfterUpdate == null && hook.AfterDelete == null,
            "an example declaring an after-hook is now compiled rather than refused, so its expressions are "
            + "exposed and this PR's scope changed");
    }

    /// <summary>
    /// The negative leg for the whole guard: a field and an entity declaring <b>none</b> of the table's
    /// features map without complaint, so the theories above are about the features rather than about the
    /// mapper refusing everything.
    /// </summary>
    [Fact]
    public void A_descriptor_declaring_none_of_the_unhonoured_features_maps_normally()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "softDelete": false, "audit": true, "fields": {
            "amount": { "type": "decimal", "precision": 8, "scale": 2 } } } } }
        """;

        var invoices = MapInline(json).Entities.Single(e => e.Name == "invoices");
        var amount = invoices.Fields.Single(f => f.Name == "amount");

        amount.Precision.ShouldBe(8);
        amount.Scale.ShouldBe(2);
        invoices.SoftDelete.ShouldBeFalse("a flag written as false is not a declaration");
        invoices.Fields.ShouldNotContain(field => field.Name == "deleted_at");
    }

    // Full-model regression freeze: the rich complex-crm fixture exercises every mapping
    // concern in one place (managed-column injection, ref FKs, tenancy, audit, softDelete,
    // renamedFrom, indexes, all field types) across multiple entities — a breadth the
    // narrower, branch-level tests above don't give. The features this build still does not
    // honour ('validation', 'default', and 'rollup' only because this fixture's own rollup
    // declares a 'where' filter) are refused by the mapper, so they are stripped at the JSON
    // level here before mapping; everything else in the fixture stays intact. None of the three
    // can reach the mapped model, so stripping them changes no snapshot line — the fixture keeps
    // them because its job is to document the descriptor format, not to be applied.
    //
    // 'computed' was the fourth until #21, and is stripped no longer: the instruction below was
    // "drop the stripping per feature as each is implemented", and this is that step. It is also
    // the one feature whose stripping had stopped being free — a computed field now DOES reach
    // the applied schema (Complex_crm_maps_the_computed_sources_it_declares is the proof), so
    // leaving it stripped would have frozen a model the mapper no longer produces. Drop the
    // stripping per feature as each is implemented.
    [Fact]
    public async Task Complex_crm_without_its_unhonoured_features_maps_to_a_stable_model()
    {
        var m = DescriptorToSchemaMapper.Map(
            AlvoDescriptor.Parse(ComplexCrmWithout("rollup", "validation", "default")));

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
            // Entity-level unhonoured features are stripped unconditionally: the fixture declares hooks on
            // two entities, and they are refused before any field is looked at, so leaving them would make
            // every one of these facts fail for the entity's reason rather than the field's.
            entity!.AsObject().Remove("hooks");
            entity.AsObject().Remove("softDelete");

            foreach (var (_, field) in entity["fields"]!.AsObject())
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
    /// Declaring a field the mapper also injects is <b>refused</b>, whatever attributes the declaration
    /// carries — the framework owns those names on an entity whose traits carry them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This branch has had three behaviours and it is worth keeping all three recorded, because the
    /// middle one looked correct. Appending unconditionally produced <b>two</b> <see cref="FieldSchema"/>
    /// entries with one name, and every later operation on the entity died with
    /// <c>ArgumentException: An item with the same key has already been added</c>. Letting the declaration
    /// win fixed that and opened two worse holes: an audited entity declaring <c>updated_at</c> as
    /// <c>{"type":"string"}</c> applied cleanly and then <b>failed every create with an internal
    /// <c>(Parameter 'value')</c> in the response body</b>, and one declaring it <c>hidden</c> applied
    /// cleanly and switched optimistic concurrency off in silence. Refusing is the only answer that is
    /// neither a duplicate nor a silent override.
    /// </para>
    /// <para>
    /// Both attribute shapes are driven — the type the framework would have used, and a wrong one — because
    /// "redundant" and "wrong" must not be told apart: a declaration that happens to match today would still
    /// be a caller-authored column standing in for one the framework writes, and the type the framework uses
    /// is not part of the descriptor's contract.
    /// </para>
    /// </remarks>
    /// <param name="column">The managed column the entity declares.</param>
    /// <param name="attributes">The declaration's attributes.</param>
    [Theory]
    [InlineData("created_by", @"""type"": ""uuid""")]
    [InlineData("created_at", @"""type"": ""datetime""")]
    [InlineData("updated_by", @"""type"": ""uuid"", ""readOnly"": true")]
    [InlineData("updated_at", @"""type"": ""datetime""")]
    [InlineData("updated_at", @"""type"": ""string""")]
    [InlineData("updated_at", @"""type"": ""datetime"", ""hidden"": true")]
    [InlineData("id", @"""type"": ""uuid""")]
    public void A_declared_managed_column_is_refused(string column, string attributes)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": { "audit": true, "fields": {
            "title": { "type": "string" },
            "{{column}}": { {{attributes}} } } } } }
        """;

        var ex = Should.Throw<InvalidDataException>(() => MapInline(json));

        ex.Message.ShouldContain($"'{column}' is a framework-managed column and cannot be declared");
        ex.Message.ShouldContain("declare it under a different name", Case.Sensitive);
    }

    /// <summary>
    /// The tenant discriminator is refused the same way, and a scoped entity is the ordinary case rather
    /// than the audited one.
    /// </summary>
    /// <remarks>
    /// It also carries the one capability the general rule removed: an earlier, narrower rule permitted
    /// <c>readOnly</c> on <c>tenant_id</c>, since that is the single managed column a caller may write. The fix
    /// suggestion has to name the replacement — a <c>create</c> rule, whose <c>WITH CHECK</c> already sees the
    /// candidate row — or an author loses the capability with nowhere to go.
    /// </remarks>
    [Fact]
    public void A_declared_tenant_id_is_refused_and_the_fix_names_a_create_rule()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": { "tenancy": "scoped", "fields": {
            "tenant_id": { "type": "uuid", "required": true, "readOnly": true } } } } }
        """;

        var ex = Should.Throw<InvalidDataException>(() => MapInline(json));

        ex.Message.ShouldContain("'tenant_id' is a framework-managed column and cannot be declared");
        ex.Message.ShouldContain("'create' rule", Case.Sensitive);
    }

    /// <summary>
    /// A field named like a managed column on an entity whose traits do <b>not</b> carry it is mapped
    /// normally — the rule is trait-scoped, never a flat name list.
    /// </summary>
    /// <remarks>
    /// An entity without <c>audit</c> may legitimately declare an ordinary <c>created_at</c>, and refusing that
    /// would refuse a field the framework does not manage. It is the same reasoning
    /// <see cref="AlvoManagedColumns"/>' own remarks give for answering membership from traits, and this is the
    /// boundary a flat name list would quietly move.
    /// </remarks>
    [Fact]
    public void A_managed_name_on_an_entity_whose_traits_do_not_carry_it_is_an_ordinary_field()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": { "fields": {
            "created_at": { "type": "datetime" },
            "deleted_at": { "type": "datetime" } } } } }
        """;

        var notes = MapInline(json).Entities.Single(e => e.Name == "notes");

        notes.Fields.Select(field => field.Name).ShouldContain("created_at");
        notes.Fields.Select(field => field.Name).ShouldContain("deleted_at");
        notes.Fields.Select(field => field.Name).ShouldBeUnique();
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

    /// <summary>
    /// The applied schema carries the <b>CEL source</b>, not a rendered expression — see
    /// <see cref="FieldSchema.ComputedExpression"/>: a <see cref="SchemaModel"/> is persisted and read back by
    /// whichever driver is registered, so one engine's spelling stored there would be DDL for the wrong engine
    /// after a provider change.
    /// </summary>
    [Fact]
    public void Map_carries_the_computed_expression_as_cel_source()
    {
        var model = DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(WithComputed));

        FieldOf(model, "invoices", "gross").ComputedExpression.ShouldBe("net * 1.2");
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
