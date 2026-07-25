using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Expressions;

namespace MMLib.Alvo.Tests.Rules;

public class PolicyEngineTests
{
    [Fact]
    public void No_rules_block_at_all_denies_every_operation()
    {
        var engine = Engine("null");

        foreach (var operation in Enum.GetValues<DataOperation>())
        {
            engine.Resolve("orders", operation, CelFixtures.Alice).IsDenied.ShouldBeTrue();
        }
    }

    [Fact]
    public void A_null_rule_for_the_requested_operation_denies()
    {
        var decision = Engine("""{"list": "true"}""").Resolve("orders", DataOperation.Get, CelFixtures.Alice);

        decision.IsDenied.ShouldBeTrue();
    }

    [Fact]
    public void True_allows_with_a_constant_true_predicate_never_a_null_one()
    {
        var decision = Engine("""{"list": "true"}""").Resolve("orders", DataOperation.List, CelFixtures.Alice);

        decision.IsDenied.ShouldBeFalse();
        decision.Using.ShouldNotBeNull();
        decision.Using!.Source.ShouldBe("true");
    }

    [Fact]
    public void List_carries_using_only()
    {
        var decision = Engine(AllTrueRules).Resolve("orders", DataOperation.List, CelFixtures.Alice);

        decision.Using.ShouldNotBeNull();
        decision.WithCheck.ShouldBeNull();
    }

    [Fact]
    public void Get_carries_using_only()
    {
        var decision = Engine(AllTrueRules).Resolve("orders", DataOperation.Get, CelFixtures.Alice);

        decision.Using.ShouldNotBeNull();
        decision.WithCheck.ShouldBeNull();
    }

    [Fact]
    public void Delete_carries_using_only()
    {
        var decision = Engine(AllTrueRules).Resolve("orders", DataOperation.Delete, CelFixtures.Alice);

        decision.Using.ShouldNotBeNull();
        decision.WithCheck.ShouldBeNull();
    }

    [Fact]
    public void Create_carries_with_check_only()
    {
        var decision = Engine(AllTrueRules).Resolve("orders", DataOperation.Create, CelFixtures.Alice);

        decision.WithCheck.ShouldNotBeNull();
        decision.Using.ShouldBeNull();
    }

    /// <summary>Two that must not be forgotten (task brief): update reuses one expression for both slots.</summary>
    [Fact]
    public void Update_reuses_one_expression_for_both_using_and_with_check()
    {
        var decision = Engine("""{"update": "owner_id == @user.id"}""")
            .Resolve("orders", DataOperation.Update, CelFixtures.Alice);

        decision.Using!.Source.ShouldBe("owner_id == @user.id");
        decision.WithCheck!.Source.ShouldBe(decision.Using.Source);
    }

    /// <summary>Two that must not be forgotten (task brief): the tenant guard runs before any rule.</summary>
    [Fact]
    public void A_scoped_entity_denies_a_context_with_no_tenant_before_any_rule_is_consulted()
    {
        var decision = Engine("""{"list": "true"}""").Resolve("orders", DataOperation.List, CelFixtures.TenantlessAlice);

        decision.IsDenied.ShouldBeTrue();
        decision.DenyReason.ShouldNotBeNull();
        decision.DenyReason.ShouldContain("tenant");
    }

    [Fact]
    public void A_tenant_scoped_entity_synthesizes_the_tenant_scope()
    {
        var decision = Engine(AllTrueRules).Resolve("orders", DataOperation.List, CelFixtures.Alice);

        decision.TenantScope.ShouldNotBeNull();
        decision.TenantScope!.Source.ShouldBe("tenant_id == @tenant.id");
    }

    [Fact]
    public void A_global_entity_ignores_the_tenant_entirely()
    {
        var decision = Engine(AllTrueRules, TenancyMode.Global).Resolve("orders", DataOperation.List, CelFixtures.TenantlessAlice);

        decision.IsDenied.ShouldBeFalse();
        decision.TenantScope.ShouldBeNull();
    }

    [Fact]
    public void An_unknown_entity_denies_rather_than_throwing()
    {
        var decision = Engine(AllTrueRules).Resolve("does-not-exist", DataOperation.List, CelFixtures.Alice);

        decision.IsDenied.ShouldBeTrue();
    }

    [Fact]
    public void A_static_true_hidden_field_is_always_in_the_mask()
    {
        var descriptor = Descriptor(AllTrueRules, fields: new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.Uuid, Hidden = BoolOrCel.FromBoolean(true) },
        });

        var decision = Build(descriptor, TenancyMode.Scoped).Resolve("orders", DataOperation.List, CelFixtures.Alice);

        decision.HiddenFields.ShouldContain("owner_id");
    }

    [Fact]
    public void A_static_false_hidden_field_never_appears_in_the_mask()
    {
        var descriptor = Descriptor(AllTrueRules, fields: new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.Uuid, Hidden = BoolOrCel.FromBoolean(false) },
        });

        var decision = Build(descriptor, TenancyMode.Scoped).Resolve("orders", DataOperation.List, CelFixtures.Alice);

        decision.HiddenFields.ShouldNotContain("owner_id");
    }

    [Fact]
    public void A_context_only_readonly_expression_is_evaluated_per_request()
    {
        var descriptor = Descriptor(AllTrueRules, fields: new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["status"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.Enum, Values = ["draft", "approved"], ReadOnly = BoolOrCel.FromExpression("'editor' in @user.roles") },
        });
        var engine = Build(descriptor, TenancyMode.Scoped);

        engine.Resolve("orders", DataOperation.List, CelFixtures.Editor).ReadOnlyFields.ShouldContain("status");
        engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).ReadOnlyFields.ShouldNotContain("status");
    }

    private const string AllTrueRules = """{"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}""";

    private static MMLib.Alvo.Rules.Internal.PolicyEngine Engine(string rulesJson, TenancyMode tenancy = TenancyMode.Scoped) =>
        Build(Descriptor(rulesJson), tenancy);

    private static MMLib.Alvo.Rules.Internal.PolicyEngine Build(AlvoDescriptor descriptor, TenancyMode tenancy)
    {
        var schema = new SchemaModel([CelFixtures.Orders with { Tenancy = tenancy }]);
        var catalog = PolicyCatalog.Build(descriptor, schema, CelFixtures.Compiler);
        return new MMLib.Alvo.Rules.Internal.PolicyEngine(() => catalog);
    }

    private static AlvoDescriptor Descriptor(string rulesJson, IReadOnlyDictionary<string, FieldDescriptor>? fields = null)
    {
        var json = $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "test",
          "entities": {
            "orders": {
              "fields": {},
              "rules": {{rulesJson}}
            }
          }
        }
        """;

        var descriptor = AlvoDescriptor.Parse(json);
        if (fields is null)
        {
            return descriptor;
        }

        var entity = descriptor.Entities["orders"] with { Fields = fields };
        return descriptor with { Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal) { ["orders"] = entity } };
    }
}
