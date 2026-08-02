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

    private static SchemaModel Schema() => new([CelFixtures.Orders]);

    private static EntityDescriptor Entity(AccessRules? rules = null, IReadOnlyDictionary<string, FieldDescriptor>? fields = null) =>
        new() { Fields = fields ?? new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal), Rules = rules };

    private static AlvoDescriptor Descriptor(string entityName, EntityDescriptor entity) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "test",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal) { [entityName] = entity },
    };
}
