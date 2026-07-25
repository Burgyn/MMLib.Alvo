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
    public void A_blank_entity_name_denies_rather_than_throwing()
    {
        var decision = Engine(AllTrueRules).Resolve("   ", DataOperation.List, CelFixtures.Alice);

        decision.IsDenied.ShouldBeTrue();
    }

    /// <summary>
    /// An unknown entity must stay indistinguishable from a known entity with no rule for this
    /// operation: same client-facing text for both, so an attacker probing entity names via
    /// <see cref="PolicyDecision.DenyReason"/> cannot enumerate which entities exist.
    /// </summary>
    [Fact]
    public void An_unknown_entity_and_a_known_entity_with_no_matching_rule_deny_with_the_same_reason()
    {
        var unknownEntity = Engine("""{"get": "true"}""").Resolve("does-not-exist", DataOperation.List, CelFixtures.Alice);
        var noRuleForOperation = Engine("""{"get": "true"}""").Resolve("orders", DataOperation.List, CelFixtures.Alice);

        unknownEntity.DenyReason.ShouldBe(noRuleForOperation.DenyReason);
    }

    /// <summary>
    /// The caller-supplied entity name must never be echoed into the client-facing deny reason —
    /// it is attacker-controlled and a log-injection vector (a newline-bearing name) if it were.
    /// </summary>
    [Fact]
    public void The_deny_reason_for_an_unknown_entity_never_echoes_the_entity_name()
    {
        const string InjectionAttempt = "orders\nSTATUS 500 forged-log-line";

        var decision = Engine(AllTrueRules).Resolve(InjectionAttempt, DataOperation.List, CelFixtures.Alice);

        decision.DenyReason.ShouldNotBeNull();
        decision.DenyReason.ShouldNotContain(InjectionAttempt);
        decision.DenyReason.ShouldNotContain('\n');
    }

    /// <summary>
    /// The tenant guard and the operation lookup deny for different, distinguishable reasons: a
    /// no-rules-block entity with a tenantless caller must still report the guard-first reason
    /// (naming "tenant"), not the lookup-first "no policy allows" text — proving the guard really
    /// runs before the operation lookup rather than the two coincidentally agreeing because both
    /// paths were exercised with the same trivial rule.
    /// </summary>
    [Fact]
    public void The_tenant_guard_and_the_operation_lookup_deny_with_different_reasons()
    {
        var guardFirst = Engine("null").Resolve("orders", DataOperation.List, CelFixtures.TenantlessAlice);
        var lookupFirst = Engine("null").Resolve("orders", DataOperation.List, CelFixtures.Alice);

        guardFirst.DenyReason.ShouldNotBeNull();
        lookupFirst.DenyReason.ShouldNotBeNull();
        guardFirst.DenyReason.ShouldContain("tenant");
        lookupFirst.DenyReason.ShouldNotContain("tenant");
        guardFirst.DenyReason.ShouldNotBe(lookupFirst.DenyReason);
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

    /// <summary>
    /// The exact scenario the fail-open masking finding named: a <c>hidden</c> expression over
    /// <c>@tenant.id</c> evaluated for a tenantless caller (on a <c>Global</c> entity, so the tenant
    /// guard itself does not intervene first). This particular negated expression evaluates to a
    /// definite <see langword="true"/> either way <c>CelInterpreter</c> collapses a null-operand
    /// comparison (documented as always exactly <see langword="false"/>/<see langword="true"/>, never
    /// a third value) — the divergence between the old "false unless exactly true" masking and the
    /// fixed "masked unless exactly false" only shows up for a malformed/exceptional evaluation (see
    /// <c>CelInterpreterTests</c>'s <c>EvaluateMask</c> tests). This test pins the documented, correct
    /// behavior for the scenario as named, as a regression guard against a future change to the
    /// null-rule reintroducing real ambiguity here.
    /// </summary>
    [Fact]
    public void A_hidden_expression_over_tenant_id_masks_a_tenantless_caller()
    {
        var descriptor = Descriptor(AllTrueRules, fields: new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
        {
            ["owner_id"] = new() { Type = MMLib.Alvo.Descriptor.FieldType.Uuid, Hidden = BoolOrCel.FromExpression("!(@tenant.id == @user.id)") },
        });
        var engine = Build(descriptor, TenancyMode.Global);

        var decision = engine.Resolve("orders", DataOperation.List, CelFixtures.TenantlessAlice);

        decision.IsDenied.ShouldBeFalse();
        decision.HiddenFields.ShouldContain("owner_id");
    }

    private const string AllTrueRules = """{"list": "true", "get": "true", "create": "true", "update": "true", "delete": "true"}""";

    private static MMLib.Alvo.Rules.Internal.PolicyEngine Engine(string rulesJson, TenancyMode tenancy = TenancyMode.Scoped) =>
        Build(Descriptor(rulesJson), tenancy);

    private static MMLib.Alvo.Rules.Internal.PolicyEngine Build(AlvoDescriptor descriptor, TenancyMode tenancy)
    {
        var schema = new SchemaModel([CelFixtures.Orders with { Tenancy = tenancy }]);
        var catalog = PolicyCatalog.Build(descriptor, schema, CelFixtures.Compiler);
        var provider = new MMLib.Alvo.Rules.Internal.PolicyCatalogProvider();
        provider.SetCurrent(catalog);
        return new MMLib.Alvo.Rules.Internal.PolicyEngine(provider);
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
