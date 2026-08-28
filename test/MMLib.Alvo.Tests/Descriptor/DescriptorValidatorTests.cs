using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Descriptor;

public class DescriptorValidatorTests
{
    private static readonly DescriptorValidator _validator = new();

    [Fact]
    public void Valid_descriptor_passes()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "tasks": { "fields": { "title": { "type": "string" } } } } }
        """;

        _validator.Validate(json).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Schema_violation_is_a_structured_error()
    {
        // 'name' is required by the schema; omit it.
        var json = """{ "apiVersion": "alvo.dev/v1", "entities": {} }""";

        var result = _validator.Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Severity == DescriptorValidationSeverity.Error);
        result.Errors.ShouldAllBe(e => e.Message.Length > 0);
    }

    /// <summary>
    /// <c>computed</c> is <b>no longer</b> reported: #21 honours it as a stored generated column, so the
    /// validator has nothing to warn about. Kept as a fact rather than deleted, because the interesting half is
    /// that the validator's report is driven off <see cref="UnhonouredFeatures.OnAField"/> — a feature that
    /// leaves the table has to stop being reported, and a validator carrying its own copy of the list would
    /// keep telling authors to remove a key that works.
    /// </summary>
    [Fact]
    public void A_computed_field_is_no_longer_reported_now_that_it_is_honoured()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "fields": {
            "net": { "type": "decimal", "precision": 18, "scale": 2 },
            "gross": { "type": "decimal", "precision": 18, "scale": 2, "computed": "net + net" } } } } }
        """;

        var result = _validator.Validate(json);

        result.Errors.ShouldBeEmpty();
        result.IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// Every feature the shared table records is reported here as a <b>structured</b> error — a JSON pointer
    /// naming the offending key, what silently happens instead, and what to do about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven off <see cref="UnhonouredFeatures"/> rather than a per-feature list, for the reason the mapper's
    /// twin theory gives: the list was four hand-written copies and <c>validation</c> was silently dropped for
    /// a whole task. The mapper's theory proves the mapper throws for each entry; this one proves the
    /// validator reports each entry; between them there is nowhere to add a feature only one pass refuses.
    /// </para>
    /// <para>
    /// The message is asserted to say more than "unsupported": each entry must name the <em>consequence</em>,
    /// because an author told "not supported yet" removes the key and moves on, while one told the field is
    /// therefore unconstrained knows what they lost. Asserted as "not the bare word", not as a specific
    /// phrase, so the wording can improve without the fact needing an edit.
    /// </para>
    /// </remarks>
    /// <param name="path">The table entry's path.</param>
    [Theory]
    [MemberData(nameof(EveryUnhonouredFieldFeature))]
    public void Every_unhonoured_field_feature_is_a_structured_error(string path)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": {
            "lines": { "fields": { "invoice_id": { "type": "uuid" } } },
            "invoices": { "fields": {
              "amount": { "type": "decimal", "precision": 18, "scale": 2, {{DeclarationFor(path)}} } } } } }
        """;

        var error = _validator.Validate(json).Errors
            .ShouldHaveSingleItem($"'{path}' must be reported exactly once");

        error.Path.ShouldBe(
            $"/entities/invoices/fields/amount/{path}",
            "the pointer names the offending key, not merely the field carrying it");
        error.FixSuggestion.ShouldNotBeNull().Length.ShouldBeGreaterThan(20);
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
        error.Message.ShouldNotBe(
            "Not supported yet.", "every entry names what silently happens instead of the feature");
        error.Message.Length.ShouldBeGreaterThan(40);
    }

    /// <summary>The same, one layer up: <c>softDelete</c> and every hook point, each reported on its own pointer.</summary>
    /// <param name="path">The table entry's path.</param>
    [Theory]
    [MemberData(nameof(EveryUnhonouredEntityFeature))]
    public void Every_unhonoured_entity_feature_is_a_structured_error(string path)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": {
            {{DeclarationFor(path)}},
            "fields": { "title": { "type": "string" } } } } }
        """;

        var error = _validator.Validate(json).Errors
            .ShouldHaveSingleItem($"'{path}' must be reported exactly once");

        error.Path.ShouldBe($"/entities/notes/{path}");
        error.FixSuggestion.ShouldNotBeNull().Length.ShouldBeGreaterThan(20);
    }

    public static TheoryData<string> EveryUnhonouredFieldFeature() =>
        [.. UnhonouredFeatures.OnAField.Select(feature => feature.Path)];

    public static TheoryData<string> EveryUnhonouredEntityFeature() =>
        [.. UnhonouredFeatures.OnAnEntity.Select(feature => feature.Path)];

    /// <summary>A schema-valid declaration of one table entry; throws for a path it has not been taught.</summary>
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
        _ => throw new InvalidOperationException(
            $"'{path}' is in UnhonouredFeatures but DeclarationFor does not know how to declare it. Teach it "
            + "a schema-valid declaration, or the theory case would assert nothing."),
    };

    /// <summary>
    /// A field declaring two of them reports <b>both</b>, not the first — the same every-violation promise
    /// the Data API keeps, one layer down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapper stops at the first, because an exception can only carry one; that is why this pass exists
    /// beside it rather than only behind it. An author fixing them one apply at a time pays a round trip per
    /// key.
    /// </para>
    /// <para>
    /// Asserted by <em>which</em> refusals are present rather than by a total count. This descriptor is
    /// deliberately schema-valid, but a count would still couple the fact to however many findings any other
    /// pass happens to produce for the same field — and that coupling is what makes a fact fail for a reason
    /// its name does not claim. <c>computed</c> is left out for the same reason: combined with <c>default</c>
    /// it is refused by the schema pass too, which is a different statement.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_field_declaring_several_unhonoured_features_reports_all_of_them()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "invoices": { "fields": {
            "amount": {
              "type": "decimal", "precision": 18, "scale": 2,
              "validation": "value >= 0", "default": 1 } } } } }
        """;

        var reported = _validator.Validate(json).Errors.Select(error => error.Path).ToList();

        reported.ShouldContain("/entities/invoices/fields/amount/validation");
        reported.ShouldContain(
            "/entities/invoices/fields/amount/default",
            "reporting only the first costs the author an apply per key");
    }

    /// <summary>
    /// <b>The validator and the mapper agree, entity by entity, on whether <c>tenant_id</c> is a
    /// framework-managed column</b> — across the whole project-tenancy × entity-tenancy matrix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two passes answer this question from different representations: the validator from raw JSON (it must,
    /// because it runs before the descriptor is parseable) and the mapper from a typed
    /// <c>EntityDescriptor</c>. They were a line-for-line duplicate with nothing tying them, and the two answers
    /// are not independent — <b>the validator decides whether declaring <c>tenant_id</c> is refused, the mapper
    /// decides whether <c>tenant_id</c> is injected</b>. A divergence is therefore not a cosmetic mismatch: in
    /// one direction a descriptor is reported as wrong that would have applied; in the other — worse — the
    /// validator says nothing and the mapper then throws, turning a structured error a dashboard can show into
    /// an exception at apply.
    /// </para>
    /// <para>
    /// The defaulting rule is now shared (<c>DescriptorToSchemaMapper.ResolveTenancy</c>), so what this fact
    /// still protects is the pair of <em>parsings</em> and the threading Task 6 added: the project root's
    /// <c>tenancy.enabled</c> read once and passed down to each entity. Drop that threading and the
    /// <c>enabled + declares nothing</c> row fails, which is exactly the case a multi-tenant project is built
    /// from.
    /// </para>
    /// <para>
    /// Both halves are asserted from <em>observable behaviour</em> rather than by calling the two private
    /// inferences: the entity declares its own <c>tenant_id</c>, so "is it managed" shows up as the validator
    /// reporting an error at that field and the mapper throwing. A fact that compared the inferences directly
    /// would still pass if one of them stopped being consulted.
    /// </para>
    /// </remarks>
    /// <param name="projectTenancyEnabled">Whether the project's <c>tenancy.enabled</c> is on.</param>
    /// <param name="entityTenancy">What the entity declares, or <see langword="null"/> for nothing.</param>
    /// <param name="carriesTenantId">Whether the entity should therefore carry a managed <c>tenant_id</c>.</param>
    [Theory]
    [InlineData(true, null, true)]        // enabled + says nothing → scoped: the multi-tenant default
    [InlineData(true, "scoped", true)]    // enabled + explicit scoped
    [InlineData(true, "global", false)]   // enabled + explicit opt-out
    [InlineData(false, null, false)]      // disabled + says nothing → no tenancy at all
    [InlineData(false, "scoped", true)]   // disabled + entity opts in anyway
    [InlineData(false, "global", false)]  // disabled + explicit global
    public void The_validator_and_the_mapper_agree_on_which_entities_carry_a_tenant_id(
        bool projectTenancyEnabled, string? entityTenancy, bool carriesTenantId)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "tenancy": { "enabled": {{(projectTenancyEnabled ? "true" : "false")}} },
          "entities": { "notes": {
            {{DeclaredTenancy(entityTenancy)}}
            "fields": {
              "title": { "type": "string" },
              "tenant_id": { "type": "uuid" } } } } }
        """;

        var reportedByTheValidator = _validator.Validate(json).Errors
            .Any(error => error.Path == "/entities/notes/fields/tenant_id");
        var refusedByTheMapper = Record.Exception(
            () => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(json))) is InvalidDataException;

        reportedByTheValidator.ShouldBe(
            carriesTenantId,
            "the validator must treat 'tenant_id' as managed exactly when tenancy resolves to scoped "
            + $"(project enabled={projectTenancyEnabled}, entity declares '{entityTenancy ?? "nothing"}')");
        refusedByTheMapper.ShouldBe(
            reportedByTheValidator,
            "the two passes must agree: one decides whether declaring 'tenant_id' is reported, the other "
            + "whether 'tenant_id' is injected, and a descriptor accepted by the first and thrown out by the "
            + "second is a structured error nobody ever sees");
    }

    /// <summary>The entity's own <c>tenancy</c> declaration, or nothing at all.</summary>
    /// <param name="entityTenancy">The declared value, or <see langword="null"/> to declare none.</param>
    private static string DeclaredTenancy(string? entityTenancy) =>
        entityTenancy is null ? string.Empty : $@"""tenancy"": ""{entityTenancy}"",";

    /// <summary>
    /// Every hook point this build honours — the three <c>after*</c> since PR5a and the three <c>before*</c>
    /// since PR5b, so all six — named as a literal so the fact below can say which points are
    /// <em>deliberately</em> absent from the refusal table rather than merely missing from it.
    /// </summary>
    private static readonly string[] _honouredHookPoints =
        ["afterCreate", "afterUpdate", "afterDelete", "beforeCreate", "beforeUpdate", "beforeDelete"];

    /// <summary>
    /// <b>Every hook point the frozen schema declares is either refused by the table or honoured by this
    /// build</b> — asserted against <c>schema/project.schema.json</c>, not against a copy of the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the direction the table-driven theories structurally cannot cover. They are driven <em>off</em>
    /// the table, so deleting an entry shrinks their own data and they go on passing while the feature
    /// silently becomes accepted again — measured: dropping <c>afterDelete</c> left every theory green. The
    /// only assertion that catches it is one whose expected set comes from outside the code under test, which
    /// is the same argument that anchored the built-in formats to the schema's <c>format</c> enum.
    /// </para>
    /// <para>
    /// <b>PR5a narrowed it, exactly as its predecessor said it would have to.</b> The set was once asserted as
    /// exactly the schema's six, because all six were unhonoured; PR5a compiles the three <c>after*</c> points
    /// into the policy catalog, so it is now a <em>partition</em>: refused ∪ honoured = declared, with nothing
    /// in both and nothing in neither. That keeps the anchor's strength — a point dropped from the table
    /// without being implemented lands in neither half and fails here — while letting each point leave on the
    /// day it starts working. That the honoured three really are compiled is
    /// <c>Events.AfterHookCompilerTests.Every_after_point_the_schema_declares_is_compiled_onto_its_own_list</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_hook_point_the_schema_declares_is_either_refused_or_honoured()
    {
        var declared = SchemaProperties("$defs", "entity", "properties", "hooks");

        declared.ShouldBe(
            ["beforeCreate", "beforeUpdate", "beforeDelete", "afterCreate", "afterUpdate", "afterDelete"],
            ignoreOrder: true,
            "read from the frozen schema — if this changed, the schema changed and the table owes it a visit");

        var refused = UnhonouredFeatures.OnAnEntity
            .Select(feature => feature.Path)
            .Where(path => path.StartsWith("hooks/", StringComparison.Ordinal))
            .Select(path => path["hooks/".Length..])
            .ToList();

        refused.ShouldNotContain(
            point => _honouredHookPoints.Contains(point, StringComparer.Ordinal),
            "a point this build runs must not also be refused, or a descriptor declaring it is rejected for a "
            + "feature that works");
        refused.Concat(_honouredHookPoints).ShouldBe(
            declared,
            ignoreOrder: true,
            "a hook point in neither half is one that silently runs nothing again — dropped from the table "
            + "without being implemented");
    }

    /// <summary>
    /// Every path in either table names a key the frozen schema actually declares, so a typo'd entry cannot
    /// sit there matching nothing.
    /// </summary>
    /// <remarks>
    /// An entry whose path no descriptor can carry is worse than no entry: it reads as a refusal that exists
    /// and refuses nothing, and neither the mapper's theory nor the validator's would notice, because both
    /// synthesise their test descriptor from the same string.
    /// </remarks>
    [Fact]
    public void Every_unhonoured_path_names_a_key_the_schema_declares()
    {
        var fieldKeys = SchemaProperties("$defs", "field");
        var entityKeys = SchemaProperties("$defs", "entity");
        var hookPoints = SchemaProperties("$defs", "entity", "properties", "hooks");

        UnhonouredFeatures.OnAField.Select(feature => feature.Path).ShouldBeSubsetOf(fieldKeys);
        foreach (var path in UnhonouredFeatures.OnAnEntity.Select(feature => feature.Path))
        {
            var segments = path.Split('/');
            entityKeys.ShouldContain(segments[0], $"'{path}' names no key of the entity schema");
            if (segments.Length > 1)
            {
                hookPoints.ShouldContain(segments[1], $"'{path}' names no declared hook point");
            }
        }
    }

    /// <summary>
    /// The property names the frozen schema declares at one location, navigated by path.
    /// </summary>
    /// <remarks>
    /// Navigated rather than keyed on <c>$defs</c>, because the hooks object is <em>inline</em> under
    /// <c>$defs/entity/properties/hooks</c> rather than a definition of its own — a fact worth encoding here
    /// rather than working around, since a fact that cannot find its anchor asserts nothing.
    /// </remarks>
    /// <param name="path">The path from the schema root down to the node whose <c>properties</c> to read.</param>
    private static List<string> SchemaProperties(params string[] path)
    {
        JsonNode node = JsonNode.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "schema", "project.schema.json")))!;
        foreach (var segment in path)
        {
            node = node[segment]
                ?? throw new InvalidOperationException(
                    $"The schema has no '{string.Join("/", path)}' — this fact's anchor moved, so it is "
                    + "asserting against nothing until the path is corrected.");
        }

        var names = node["properties"]!.AsObject().Select(property => property.Key).ToList();
        names.ShouldNotBeEmpty($"'{string.Join("/", path)}' declares no properties");
        return names;
    }

    /// <summary>
    /// A declaration written as <c>false</c> or as an empty list is <b>not</b> a declaration, so it earns no
    /// refusal.
    /// </summary>
    /// <remarks>
    /// PR2 established the negative leg for <c>softDelete: false</c>, and the table inherits it: refusing a
    /// feature an author explicitly declined to use is a refusal they cannot act on. An empty
    /// <c>beforeUpdate: []</c> is the same statement in list form, and it is the one the per-hook-point
    /// refusal made newly possible.
    /// </remarks>
    [Fact]
    public void A_feature_declined_by_value_is_not_a_declaration()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "notes": {
            "softDelete": false,
            "hooks": { "beforeUpdate": [] },
            "fields": { "title": { "type": "string" } } } } }
        """;

        _validator.Validate(json).IsValid.ShouldBeTrue(
            "false is not 'declared', and an empty hook list asks for nothing");
    }

    /// <summary>
    /// A field whose name the Data API's query string reserves is refused at <b>apply</b>, one error per
    /// offending field, naming the field and the reserved list.
    /// </summary>
    /// <remarks>
    /// The descriptor's own field grammar accepts every reserved name, so <c>?limit=10</c> against an entity
    /// declaring a <c>limit</c> field is genuinely ambiguous. It is refused here rather than only at route mapping
    /// because the descriptor is wrong whether or not the API is mounted — an embedded host that never maps the
    /// Data API would otherwise get no refusal at all.
    /// </remarks>
    [Theory]
    [InlineData("limit")]
    [InlineData("offset")]
    [InlineData("order")]
    [InlineData("select")]
    [InlineData("after")]
    [InlineData("or")]
    [InlineData("and")]
    [InlineData("not")]
    public void Field_shadowing_a_reserved_query_parameter_is_rejected(string reserved)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "widgets": { "fields": {
            "{{reserved}}": { "type": "integer" } } } } }
        """;

        var result = _validator.Validate(json);

        result.IsValid.ShouldBeFalse();
        var shadowed = result.Errors.ShouldHaveSingleItem();
        shadowed.Path.ShouldBe($"/entities/widgets/fields/{reserved}");
        shadowed.Message.ShouldContain(reserved);
        shadowed.FixSuggestion.ShouldNotBeNull().ShouldContain("Rename the field");
    }

    /// <summary>
    /// A field whose name is merely <em>similar</em> to a reserved one is accepted. Without this the theory above
    /// would pass against a validator that refused every field name it was shown.
    /// </summary>
    [Theory]
    [InlineData("limits")]
    [InlineData("sort_order")]
    [InlineData("selected")]
    [InlineData("Limit")]
    public void Field_merely_resembling_a_reserved_query_parameter_is_accepted(string name)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "widgets": { "fields": {
            "{{name}}": { "type": "integer" } } } } }
        """;

        _validator.Validate(json).Errors.ShouldNotContain(error => error.Message.Contains("reserved"));
    }

    [Fact]
    public void Ref_to_unknown_entity_is_rejected()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { "fields": {
            "customer": { "type": "ref", "entity": "missing" } } } } }
        """;

        var result = _validator.Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Message.Contains("missing"));
    }

    [Fact]
    public void Ref_to_built_in_users_entity_is_accepted()
    {
        // "users" is never declared under "entities" (it's schema-reserved as a declared key), but
        // a ref field targeting it is a legitimate reference to the built-in auth entity, not an
        // unknown-entity error — the only thing under test here is that exemption.
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { "fields": {
            "owner": { "type": "ref", "entity": "users" } } } } }
        """;

        var result = _validator.Validate(json);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldNotContain(e => e.Message.Contains("users"));
    }

    [Fact]
    public void Malformed_json_is_a_single_structured_error()
    {
        var json = """{ "apiVersion": "alvo.dev/v1", "name": "demo", """;

        var result = _validator.Validate(json);

        var error = result.Errors.ShouldHaveSingleItem();
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
        error.Path.ShouldBe("/");
        error.Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public void Schema_violations_carry_distinct_non_constant_fix_suggestions()
    {
        var missingName = """
        { "apiVersion": "alvo.dev/v1",
          "entities": { "tasks": { "fields": { "title": { "type": "string" } } } } }
        """;
        var missingApiVersion = """
        { "name": "demo",
          "entities": { "tasks": { "fields": { "title": { "type": "string" } } } } }
        """;

        var nameError = _validator.Validate(missingName).Errors.ShouldHaveSingleItem();
        var apiVersionError = _validator.Validate(missingApiVersion).Errors.ShouldHaveSingleItem();

        nameError.FixSuggestion.ShouldNotBeNull();
        apiVersionError.FixSuggestion.ShouldNotBeNull();
        nameError.FixSuggestion.ShouldNotBe(apiVersionError.FixSuggestion);
        nameError.FixSuggestion.ShouldContain("required");
    }

    [Fact]
    public void Keywordless_schema_violation_falls_back_to_a_generic_message()
    {
        // additionalProperties:false has no per-property message from Corvus — this exercises the
        // "Value does not satisfy the schema at ..." fallback in ToError/FixSuggestionFor, not the
        // keyword-supplied message path the other tests hit.
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo", "entities": { "tasks": { "fields": { "title": { "type": "string" } } } },
          "unknownTopLevel": 1 }
        """;

        var result = _validator.Validate(json);

        var error = result.Errors.Single(e => e.Path.Contains("unknownTopLevel"));
        error.Message.ShouldBe("Value does not satisfy the schema at '#/additionalProperties/unknownTopLevel'.");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("'unknownTopLevel'");
    }

    [Fact]
    public void Multiple_simultaneous_schema_violations_are_all_reported_distinctly()
    {
        var json = """{ "apiVersion": "alvo.dev/v2", "entities": {} }""";

        var result = _validator.Validate(json);

        result.Errors.Count.ShouldBeGreaterThanOrEqualTo(2);
        result.Errors.ShouldContain(e => e.Path.Contains("apiVersion"));
        result.Errors.ShouldContain(e => e.Path.Contains("entities"));
        result.Errors.Select(e => e.Message).Distinct().Count().ShouldBe(result.Errors.Count);
    }

    [Fact]
    public void A_rule_referencing_an_unknown_column_fails_validation_not_the_request()
    {
        var result = Validate(DescriptorWithRule("list", "ownr_id == @user.id"));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error =>
            error.Path == "/entities/orders/rules/list" && error.Message.Contains("ownr_id"));
    }

    [Fact]
    public void The_singular_user_role_is_refused_at_apply_with_the_plural_fix()
    {
        var result = Validate(DescriptorWithRule("list", "@user.role == 'admin'"));

        result.Errors.ShouldContain(error => error.FixSuggestion!.Contains("in @user.roles"));
    }

    [Fact]
    public void A_row_dependent_hidden_expression_is_refused_at_apply()
    {
        var result = Validate(DescriptorWithHidden("owner_id != @user.id"));

        result.Errors.ShouldContain(error =>
            error.Path == "/entities/orders/fields/notes/hidden");
    }

    [Fact]
    public void A_context_only_hidden_expression_is_accepted()
    {
        Validate(DescriptorWithHidden("'compliance' in @user.roles")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_valid_rule_set_still_validates()
    {
        Validate(DescriptorWithRule("list", "owner_id == @user.id")).IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// Every framework-managed column the entity's traits carry is refused when declared, with the field's own
    /// JSON path and a fix suggestion — the form a dashboard or a CLI <c>validate</c> can show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapper throws for the same declaration, and that is a pair rather than a duplicate: this pass reports
    /// every offending field at once with a path, and the throw is the fail-closed belt for an apply that
    /// skipped the validator. Both read <c>ManagedColumnNames</c>, so a declaration cannot be explained one way
    /// here and another by the apply that follows it.
    /// </para>
    /// <para>
    /// <c>deleted_at</c> is driven with <c>softDelete</c>, which this build also refuses as unhonoured — so that
    /// descriptor earns <em>two</em> errors and the assertion looks for its own rather than for the only one.
    /// That is exactly why it is worth driving: <c>deleted_at</c>'s reason is unreachable through the mapper,
    /// which refuses <c>softDelete</c> before injecting anything, and that is how the old catch-all came to tell
    /// a softDelete-only entity its <c>deleted_at</c> was part of an audit trail.
    /// </para>
    /// </remarks>
    /// <param name="traits">The entity traits that make the column managed.</param>
    /// <param name="column">The managed column the entity declares.</param>
    /// <param name="mentions">
    /// A phrase this column's <em>message</em> must contain, so the per-name reason is really per name. The
    /// replacement for the lost <c>readOnly</c>-on-<c>tenant_id</c> narrowing lives in the <em>fix</em> half
    /// instead, and is asserted where it belongs by
    /// <c>DescriptorToSchemaMapperTests.A_declared_tenant_id_is_refused_and_the_fix_names_a_create_rule</c>.
    /// </param>
    [Theory]
    [InlineData(@"""audit"": true", "created_at", "every create")]
    [InlineData(@"""audit"": true", "created_by", "audit trail")]
    [InlineData(@"""audit"": true", "updated_at", "If-Match")]
    [InlineData(@"""audit"": true", "updated_by", "audit trail")]
    [InlineData(@"""tenancy"": ""scoped""", "tenant_id", "discriminator")]
    [InlineData(@"""softDelete"": true", "deleted_at", "recoverable")]
    [InlineData(@"""audit"": true", "id", "row key")]
    public void A_declared_framework_managed_column_is_reported_with_its_path_and_its_own_reason(
        string traits, string column, string mentions)
    {
        var json = $$"""
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { {{traits}}, "fields": {
            "title": { "type": "string" },
            "{{column}}": { "type": "datetime" } } } } }
        """;

        var error = Validate(json).Errors
            .Where(candidate => candidate.Path == $"/entities/orders/fields/{column}")
            .ShouldHaveSingleItem();

        error.Message.ShouldContain($"'{column}' is a framework-managed column and cannot be declared");
        error.Message.ShouldContain(mentions, Case.Sensitive, "the reason must be this column's, not a catch-all");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("declare it under a different name", Case.Sensitive);
    }

    /// <summary>
    /// An entity that says nothing about tenancy, in a project that turns tenancy <b>on</b>, still carries a
    /// managed <c>tenant_id</c> — so declaring it is refused there too.
    /// </summary>
    /// <remarks>
    /// The case this pass would otherwise have missed. <c>tenant_id</c>'s membership is a <em>project</em>-level
    /// answer for an entity with no <c>tenancy</c> of its own, so reading only the entity would under-report
    /// exactly the entities a multi-tenant project is built from — the mapper would still refuse them, but with
    /// an exception instead of a path and a fix.
    /// </remarks>
    [Fact]
    public void A_declared_tenant_id_is_refused_when_the_project_turns_tenancy_on()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "tenancy": { "enabled": true },
          "entities": { "orders": { "fields": {
            "tenant_id": { "type": "uuid" } } } } }
        """;

        Validate(json).Errors.ShouldContain(error => error.Path == "/entities/orders/fields/tenant_id");
    }

    /// <summary>
    /// A field named like a managed column on an entity whose traits do <b>not</b> carry it validates —
    /// trait-scoped, never a flat name list.
    /// </summary>
    [Fact]
    public void A_managed_name_the_entity_does_not_manage_is_an_ordinary_field()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { "fields": {
            "created_at": { "type": "datetime" },
            "updated_at": { "type": "datetime" },
            "deleted_at": { "type": "datetime" } } } } }
        """;

        Validate(json).IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// Two declared managed columns produce <b>two</b> errors rather than the first — which is the whole reason
    /// a semantic pass exists beside the mapper's throw.
    /// </summary>
    [Fact]
    public void Every_declared_managed_column_is_reported_rather_than_the_first()
    {
        var json = """
        { "apiVersion": "alvo.dev/v1", "name": "demo",
          "entities": { "orders": { "audit": true, "fields": {
            "created_at": { "type": "datetime" },
            "updated_at": { "type": "datetime" } } } } }
        """;

        Validate(json).Errors.Select(error => error.Path).ShouldBe(
            ["/entities/orders/fields/created_at", "/entities/orders/fields/updated_at"],
            ignoreOrder: true,
            "an agent must see every fix it needs in one round trip");
    }

    private static DescriptorValidationResult Validate(string json) => _validator.Validate(json);

    private static string DescriptorWithRule(string operation, string expression) => $$"""
    { "apiVersion": "alvo.dev/v1", "name": "demo",
      "entities": { "orders": {
        "fields": {
          "owner_id": { "type": "uuid" },
          "notes": { "type": "string" }
        },
        "rules": { "{{operation}}": "{{expression}}" }
      } } }
    """;

    private static string DescriptorWithHidden(string expression) => $$"""
    { "apiVersion": "alvo.dev/v1", "name": "demo",
      "auth": { "roles": ["compliance"] },
      "entities": { "orders": {
        "fields": {
          "owner_id": { "type": "uuid" },
          "notes": { "type": "string", "hidden": "{{expression}}" }
        }
      } } }
    """;
}
