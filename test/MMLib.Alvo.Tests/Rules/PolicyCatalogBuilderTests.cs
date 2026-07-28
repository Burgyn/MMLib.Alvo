using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

namespace MMLib.Alvo.Tests.Rules;

public class PolicyCatalogBuilderTests
{
    [Fact]
    public void A_rule_referencing_an_unknown_column_fails_with_the_rules_path()
    {
        var descriptor = Descriptor("orders", Entity(rules: new AccessRules { List = "no_such_column == 1" }));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        errors.ShouldContain(e => e.Path == "/entities/orders/rules/list");
    }

    [Fact]
    public void A_row_dependent_hidden_flag_fails_with_a_deferral_fix_suggestion()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new()
            {
                Type = MMLib.Alvo.Descriptor.FieldType.Uuid,
                Hidden = BoolOrCel.FromExpression("owner_id != @user.id"),
            },
        };
        var descriptor = Descriptor("orders", Entity(fields: fields));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/orders/fields/owner_id/hidden");
        error.FixSuggestion.ShouldNotBeNull();
        error.FixSuggestion.ShouldContain("defer");
    }

    [Fact]
    public void A_row_dependent_readonly_flag_fails_with_a_deferral_fix_suggestion()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new()
            {
                Type = MMLib.Alvo.Descriptor.FieldType.Uuid,
                ReadOnly = BoolOrCel.FromExpression("owner_id != @user.id"),
            },
        };
        var descriptor = Descriptor("orders", Entity(fields: fields));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/orders/fields/owner_id/readOnly");
        error.FixSuggestion.ShouldNotBeNull();
        error.FixSuggestion.ShouldContain("defer");
    }

    /// <summary>
    /// Decision 3's loudest guarantee: a <c>Scoped</c> entity with no <c>tenant_id</c> field fails at
    /// build, naming the entity's <c>/tenancy</c> path — not a silently-null <c>TenantScope</c> that
    /// would let every tenant read every row.
    /// </summary>
    [Fact]
    public void A_scoped_entity_with_no_tenant_id_field_fails_at_the_tenancy_path()
    {
        var schemaWithoutTenantId = new SchemaModel([
            new EntitySchema
            {
                Name = "orders",
                Tenancy = TenancyMode.Scoped,
                Fields = [new FieldSchema { Name = "id", Type = MMLib.Alvo.Schema.FieldType.Uuid }],
            },
        ]);
        var descriptor = Descriptor("orders", Entity());

        PolicyCatalog.TryBuild(descriptor, schemaWithoutTenantId, CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        errors.ShouldContain(e => e.Path == "/entities/orders/tenancy");
    }

    /// <summary>
    /// Deny-by-default (Important 2): a node kind <see cref="PolicyCatalogBuilder.ReferencesRowField"/>
    /// was never taught about — here <see cref="CelChanged"/>, itself already row-dependent and, before
    /// this fix, silently absent from the switch — must count as row-dependent, never as safely
    /// context-only. A permissive default arm here would let an as-yet-unrecognized construct compile
    /// into an unmasked field mask.
    /// </summary>
    [Fact]
    public void ReferencesRowField_denies_by_default_for_an_unrecognized_construct()
    {
        PolicyCatalogBuilder.ReferencesRowField(new CelChanged("owner_id")).ShouldBeTrue();
    }

    [Fact]
    public void ReferencesRowField_allows_a_literal_or_context_reference()
    {
        PolicyCatalogBuilder.ReferencesRowField(new CelLiteral(CelValueType.Bool, true)).ShouldBeFalse();
        PolicyCatalogBuilder.ReferencesRowField(new CelContextRef(CelContextValue.TenantId, CelValueType.Uuid)).ShouldBeFalse();
    }

    [Fact]
    public void At_user_role_singular_fails_with_the_plural_fix()
    {
        var descriptor = Descriptor("orders", Entity(rules: new AccessRules { List = "@user.role == @user.id" }));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/orders/rules/list");
        error.FixSuggestion.ShouldNotBeNull();
        error.FixSuggestion.ShouldContain("@user.roles");
    }

    [Fact]
    public void A_valid_descriptor_builds_and_update_reuses_the_same_source_for_using_and_with_check()
    {
        var descriptor = Descriptor("orders", Entity(rules: new AccessRules { Update = "owner_id == @user.id" }));

        var catalog = PolicyCatalog.Build(descriptor, Schema(), CelFixtures.Compiler);

        catalog.TryGetEntity("orders", out var policy).ShouldBeTrue();
        var update = policy.Operations[DataOperation.Update];
        update.Using.ShouldNotBeNull();
        update.WithCheck.ShouldNotBeNull();
        update.Using.ShouldBeSameAs(update.WithCheck);
        update.Using!.Source.ShouldBe("owner_id == @user.id");
    }

    [Fact]
    public void Build_throws_a_descriptor_validation_exception_for_an_invalid_rule()
    {
        var descriptor = Descriptor("orders", Entity(rules: new AccessRules { List = "no_such_column == 1" }));

        Should.Throw<DescriptorValidationException>(() => PolicyCatalog.Build(descriptor, Schema(), CelFixtures.Compiler));
    }

    [Fact]
    public void A_static_true_hidden_flag_needs_no_compilation_and_always_masks_the_field()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.Uuid, Hidden = BoolOrCel.FromBoolean(true) },
        };
        var descriptor = Descriptor("orders", Entity(fields: fields));

        var catalog = PolicyCatalog.Build(descriptor, Schema(), CelFixtures.Compiler);

        catalog.TryGetEntity("orders", out var policy).ShouldBeTrue();
        policy.Hidden.ShouldContainKey("owner_id");
        policy.Hidden["owner_id"].AlwaysOn.ShouldBeTrue();
    }

    /// <summary>
    /// A typo'd role literal is a silent authorization change — <c>'amdin' in @user.roles</c> compiles,
    /// type-checks, and then simply never matches, so a rule an author wrote to admit admins admits
    /// nobody (or, on the negated form, everybody). The compiler cannot catch it: it has no role catalog.
    /// The catalog builder does, and reports it on the offending rule's own JSON path with the same
    /// "did you mean" shape an unknown field or enum value gets.
    /// </summary>
    [Fact]
    public void A_role_literal_that_is_not_a_declared_role_fails_with_a_did_you_mean_fix()
    {
        var descriptor = Descriptor("orders", Entity(rules: new AccessRules { List = "'amdin' in @user.roles" }));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/orders/rules/list");
        error.FixSuggestion.ShouldNotBeNull();
        error.FixSuggestion.ShouldContain("admin");
    }

    /// <summary>The negated form is the dangerous one — a typo there widens access instead of narrowing it.</summary>
    [Fact]
    public void A_negated_role_literal_that_is_not_a_declared_role_also_fails()
    {
        var descriptor = Descriptor("orders", Entity(rules: new AccessRules { Get = "!('amdin' in @user.roles)" }));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        errors.ShouldContain(e => e.Path == "/entities/orders/rules/get");
    }

    [Fact]
    public void A_role_declared_in_auth_roles_is_accepted()
    {
        var descriptor = Descriptor("orders", Entity(rules: new AccessRules { List = "'editor' in @user.roles" }))
            with
        { Auth = new MMLib.Alvo.Descriptor.Auth { Roles = ["editor"] } };

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeTrue();

        errors.ShouldBeEmpty();
        catalog.ShouldNotBeNull();
    }

    [Fact]
    public void A_built_in_role_needs_no_auth_roles_declaration()
    {
        var descriptor = Descriptor("orders", Entity(rules: new AccessRules { List = "'authenticated' in @user.roles" }));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out _, out var errors).ShouldBeTrue();

        errors.ShouldBeEmpty();
    }

    /// <summary>A field-backed membership test names no role, so there is nothing to validate and it must still build.</summary>
    [Fact]
    public void A_field_backed_membership_test_is_not_treated_as_a_role_literal()
    {
        var descriptor = Descriptor("orders", Entity(rules: new AccessRules { List = "status in @user.roles" }));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out _, out var errors).ShouldBeTrue();

        errors.ShouldBeEmpty();
    }

    /// <summary>A field flag is a Rule-profile expression too, so its role literals get the same check.</summary>
    [Fact]
    public void A_role_literal_in_a_hidden_flag_is_validated_on_the_flags_own_path()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new()
            {
                Type = MMLib.Alvo.Descriptor.FieldType.Uuid,
                Hidden = BoolOrCel.FromExpression("!('amdin' in @user.roles)"),
            },
        };
        var descriptor = Descriptor("orders", Entity(fields: fields));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        errors.ShouldContain(e => e.Path == "/entities/orders/fields/owner_id/hidden");
    }

    /// <summary>
    /// The framework owns the row key, and the read path replaces a masked field with a projected typed SQL
    /// <c>NULL</c> — which the key can never be, because EF re-marks a key property required whatever the
    /// model asked for. A <c>hidden</c> flag on <c>id</c> would therefore fail when the row was
    /// materialised, with a different exception type per engine. Alvo's rule is that a bad descriptor fails
    /// at save, so it is refused here.
    /// </summary>
    [Theory]
    [InlineData("hidden")]
    [InlineData("readOnly")]
    public void A_field_flag_on_the_framework_owned_key_is_refused_at_save(string flag)
    {
        var descriptor = Descriptor("orders", Entity(fields: Flagged("id", flag)));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe($"/entities/orders/fields/id/{flag}");
        error.FixSuggestion.ShouldNotBeNull();
    }

    /// <summary>
    /// No framework-managed column may be marked <c>hidden</c>, because masking one switches off framework
    /// behaviour the descriptor never asked to lose — and nothing raises when it does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>updated_at</c> is the case that motivated the rule and the worst of them: it is the column
    /// <c>AlvoManagedColumns.VersionColumn</c> names, so masking it leaves the HTTP layer with no <c>ETag</c>
    /// to hand out and a caller with no <c>If-Match</c> to send — <b>optimistic concurrency off, silently, for
    /// that entity</b>. It is reachable because <c>DescriptorToSchemaMapper.AddManagedColumn</c> injects a
    /// managed column only when the entity does not declare a field of that name, so an author's own
    /// declaration wins and can carry any flag the schema allows.
    /// </para>
    /// <para>
    /// Driven per column rather than asserted over the set, so a rule that happened to cover only
    /// <c>updated_at</c> — or only the audit four — fails on the others. The entity is scoped <em>and</em>
    /// audited so every managed name is in its trait set at once; <c>id</c> stays with its own fact above,
    /// which carries its own distinct reason (a masked key cannot materialise at all).
    /// </para>
    /// </remarks>
    /// <param name="column">The framework-managed column the flag names.</param>
    [Theory]
    [InlineData("tenant_id")]
    [InlineData("created_at")]
    [InlineData("created_by")]
    [InlineData("updated_at")]
    [InlineData("updated_by")]
    public void A_hidden_flag_on_a_framework_managed_column_is_refused_at_save(string column)
    {
        var descriptor = Descriptor("orders", Entity(fields: Flagged(column, "hidden")));

        PolicyCatalog.TryBuild(descriptor, ManagedSchema(), CelFixtures.Compiler, out var catalog, out var errors)
            .ShouldBeFalse();

        catalog.ShouldBeNull();
        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe($"/entities/orders/fields/{column}/hidden");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain(
            "declare it under a different name",
            Case.Sensitive,
            "the usual cause is an author who wanted a column of their own, so the fix has to say so");
    }

    /// <summary>
    /// <c>readOnly</c> on <c>tenant_id</c> is <b>accepted</b> — the deliberate limit of the rule above, and
    /// the reason it is written for one flag rather than both.
    /// </summary>
    /// <remarks>
    /// <c>tenant_id</c> is the one managed column a caller may legitimately write, and only on a create
    /// (<c>AlvoManagedColumns.IsCallerWritable</c>), so marking it read-only is a real narrowing an author may
    /// want rather than a mistake. Refusing every flag on every managed column would have taken it away, and
    /// a rule with no fact for its own boundary is a rule whose boundary moves by accident.
    /// </remarks>
    [Fact]
    public void A_read_only_flag_on_the_one_caller_writable_managed_column_is_accepted()
    {
        var descriptor = Descriptor("orders", Entity(fields: Flagged("tenant_id", "readOnly")));

        PolicyCatalog.TryBuild(descriptor, ManagedSchema(), CelFixtures.Compiler, out var catalog, out var errors)
            .ShouldBeTrue(string.Join(" ", errors.Select(error => error.Message)));

        catalog.ShouldNotBeNull();
    }

    /// <summary>
    /// A field named like a managed column on an entity that does <b>not</b> carry it stays flaggable — the
    /// rule is answered from the entity's traits, never from a name list.
    /// </summary>
    /// <remarks>
    /// An entity without <c>audit</c> may legitimately declare an ordinary <c>created_at</c>, and refusing a
    /// <c>hidden</c> flag on that would refuse a field the framework does not manage. It is the same reasoning
    /// <c>AlvoManagedColumns</c>' own remarks give for answering membership from traits, and this is where the
    /// two would silently diverge.
    /// </remarks>
    [Fact]
    public void A_hidden_flag_on_a_managed_name_the_entity_does_not_manage_is_accepted()
    {
        var descriptor = Descriptor("orders", Entity(fields: Flagged("created_at", "hidden")));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors)
            .ShouldBeTrue(string.Join(" ", errors.Select(error => error.Message)));

        catalog.ShouldNotBeNull();
    }

    /// <summary>
    /// The sibling half of DoD criterion 3 — "a rule naming a nonexistent column fails at save, not at
    /// request time". A flag naming a field the schema does not contain was silently accepted and then
    /// masked nothing, so a typo in a <c>hidden</c> flag quietly exposed the field it meant to hide.
    /// </summary>
    [Theory]
    [InlineData("hidden")]
    [InlineData("readOnly")]
    public void A_field_flag_on_a_field_the_schema_does_not_have_is_refused_at_save(string flag)
    {
        var descriptor = Descriptor("orders", Entity(fields: Flagged("secret_note", flag)));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out var catalog, out var errors).ShouldBeFalse();

        catalog.ShouldBeNull();
        errors.ShouldHaveSingleItem().Path.ShouldBe($"/entities/orders/fields/secret_note/{flag}");
    }

    /// <summary>A typo is the likeliest cause, so the fix names the nearest declared field.</summary>
    [Fact]
    public void An_unknown_flagged_field_suggests_the_nearest_declared_one()
    {
        var descriptor = Descriptor("orders", Entity(fields: Flagged("ownerid", "hidden")));

        PolicyCatalog.TryBuild(descriptor, Schema(), CelFixtures.Compiler, out _, out var errors).ShouldBeFalse();

        errors.ShouldHaveSingleItem().FixSuggestion.ShouldNotBeNull().ShouldContain("owner_id");
    }

    /// <summary>
    /// A flag that is present but <see langword="false"/> masks nothing, so it must still be validated —
    /// otherwise the refusal above could be bypassed by writing the mistake as <c>false</c> today and
    /// flipping it to <c>true</c> later, when nothing re-validates.
    /// </summary>
    [Fact]
    public void A_false_flag_on_an_unknown_field_is_refused_too()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["secret_note"] = new()
            {
                Type = MMLib.Alvo.Descriptor.FieldType.String,
                Hidden = BoolOrCel.FromBoolean(false),
            },
        };

        PolicyCatalog.TryBuild(
            Descriptor("orders", Entity(fields: fields)), Schema(), CelFixtures.Compiler, out _, out var errors)
            .ShouldBeFalse();

        errors.ShouldHaveSingleItem().Path.ShouldBe("/entities/orders/fields/secret_note/hidden");
    }

    /// <summary>
    /// A field descriptor carrying no flag at all is the ordinary case — every entity declares its fields —
    /// and must not be validated as if it did, or every descriptor would have to flag every field.
    /// </summary>
    [Fact]
    public void A_field_descriptor_with_no_flag_is_not_refused()
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["id"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.Uuid },
            ["owner_id"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.Uuid },
        };

        PolicyCatalog.TryBuild(
            Descriptor("orders", Entity(fields: fields)), Schema(), CelFixtures.Compiler, out var catalog, out var errors)
            .ShouldBeTrue();

        catalog.ShouldNotBeNull();
        errors.ShouldBeEmpty();
    }

    private static Dictionary<string, FieldDescriptor> Flagged(string field, string flag) =>
        new(StringComparer.Ordinal)
        {
            [field] = new()
            {
                Type = MMLib.Alvo.Descriptor.FieldType.Uuid,
                Hidden = flag == "hidden" ? BoolOrCel.FromBoolean(true) : null,
                ReadOnly = flag == "readOnly" ? BoolOrCel.FromBoolean(true) : null,
            },
        };

    private static SchemaModel Schema() => new([CelFixtures.Orders]);

    /// <summary>
    /// <see cref="CelFixtures.Orders"/> as a <em>scoped and audited</em> entity carrying every
    /// framework-managed column at once, so one theory covers the whole set.
    /// </summary>
    /// <remarks>
    /// <see cref="CelFixtures.Orders"/> declares <c>created_at</c> as an ordinary field and does not declare
    /// <c>audit</c>, which is exactly the case
    /// <see cref="A_hidden_flag_on_a_managed_name_the_entity_does_not_manage_is_accepted"/> needs — so the two
    /// schemas have to differ, rather than one being edited to serve both.
    /// </remarks>
    private static SchemaModel ManagedSchema() => new([
        CelFixtures.Orders with
        {
            Audit = true,
            Fields =
            [
                .. CelFixtures.Orders.Fields,
                new FieldSchema { Name = "created_by", Type = MMLib.Alvo.Schema.FieldType.Uuid, Nullable = true },
                new FieldSchema { Name = "updated_at", Type = MMLib.Alvo.Schema.FieldType.DateTime, Nullable = true },
                new FieldSchema { Name = "updated_by", Type = MMLib.Alvo.Schema.FieldType.Uuid, Nullable = true },
            ],
        },
    ]);

    private static EntityDescriptor Entity(AccessRules? rules = null, IReadOnlyDictionary<string, FieldDescriptor>? fields = null) =>
        new() { Fields = fields ?? new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal), Rules = rules };

    private static AlvoDescriptor Descriptor(string entityName, EntityDescriptor entity) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "test",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal) { [entityName] = entity },
    };
}
