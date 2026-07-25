using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
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
