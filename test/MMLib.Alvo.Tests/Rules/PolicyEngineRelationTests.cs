using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tests.Expressions;

using NSubstitute;

using System.Text.Json.Nodes;

using static MMLib.Alvo.Tests.Rules.RelationDescriptors;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// The engine binds every relation term to the related entity's <c>list</c> decision for the same caller: the related
/// rule and tenant scope, or a constant-false scope when that decision is a denial. The bound tree is cached per
/// catalog, entity, operation, slot, depth and the presence of the caller's identity and tenant — and nothing else
/// that changes the decision, so no caller is ever served a scope bound for another.
/// </summary>
public sealed class PolicyEngineRelationTests
{
    private const string OrdersByRegion = "customer_id.region == 'eu'";

    private static readonly AlvoContext _anonymousInAcme = AlvoContext.Anonymous with { Tenant = CelFixtures.Alice.Tenant };

    [Fact]
    public void A_relation_is_bound_to_the_related_list_using_and_tenant_scope()
    {
        var engine = Engine(Shop("owner_id == @user.id", customersTenancy: "scoped"));

        var scope = BoundScope(engine.Resolve("orders", DataOperation.List, CelFixtures.Alice));

        scope.Denied.ShouldBeFalse();
        scope.Using.ShouldNotBeNull().Source.ShouldBe("owner_id == @user.id");
        scope.TenantScope.ShouldNotBeNull().Source.ShouldBe("tenant_id == @tenant.id");
    }

    public static TheoryData<string, string?, string, AlvoContext> DeniedRelatedLists() => new()
    {
        { "no list rule", null, "global", CelFixtures.Alice },
        { "a scoped related entity, a tenantless caller", "true", "scoped", CelFixtures.TenantlessAlice },
        { "a related rule reading the caller, an anonymous one", "owner_id == @user.id", "global", _anonymousInAcme },
    };

    [Theory]
    [MemberData(nameof(DeniedRelatedLists))]
    public void A_denied_related_list_binds_a_constant_false_scope(string why, string? customersList, string tenancy, AlvoContext caller)
    {
        _ = why;
        var engine = Engine(Shop(customersList, customersTenancy: tenancy));

        var decision = engine.Resolve("orders", DataOperation.List, caller);

        decision.IsDenied.ShouldBeFalse();
        BoundScope(decision).ShouldBeSameAs(RelationScope.Deny);
    }

    [Fact]
    public void A_related_rule_s_own_relation_is_bound_recursively()
    {
        var engine = Engine(Shop("account_id.owner_id == @user.id"));

        var scope = BoundScope(engine.Resolve("orders", DataOperation.List, CelFixtures.Alice));

        var inner = scope.Using.ShouldNotBeNull().Root.ShouldBeOfType<CelRelation>();
        inner.Related.Name.ShouldBe("accounts");
        inner.Scope.ShouldNotBeNull().Denied.ShouldBeFalse();
    }

    [Fact]
    public void The_bound_scope_is_cached_per_presence_of_user_and_tenant()
    {
        var engine = Engine(Shop("owner_id == @user.id && region_id == @tenant.id"));
        AlvoContext[] callers =
        [
            CelFixtures.Alice, CelFixtures.TenantlessAlice, _anonymousInAcme, AlvoContext.Anonymous,
        ];

        var first = callers.Select(caller => engine.Resolve("orders", DataOperation.List, caller).Using!.Root).ToList();
        var again = callers.Select(caller => engine.Resolve("orders", DataOperation.List, caller with { }).Using!.Root).ToList();

        first.Zip(again).ShouldAllBe(pair => ReferenceEquals(pair.First, pair.Second));
        first.Distinct(ReferenceEqualityComparer.Instance).Count().ShouldBe(4);
        Scope(first[0]).Denied.ShouldBeFalse();
        first.Skip(1).ShouldAllBe(bound => Scope(bound).Denied);
    }

    [Fact]
    public void An_anonymous_caller_after_an_authenticated_one_gets_the_denied_scope()
    {
        var engine = Engine(Shop("owner_id == @user.id"));
        BoundScope(engine.Resolve("orders", DataOperation.List, CelFixtures.Alice)).Denied.ShouldBeFalse();

        var anonymous = BoundScope(engine.Resolve("orders", DataOperation.List, _anonymousInAcme));

        anonymous.ShouldBeSameAs(RelationScope.Deny);
    }

    [Fact]
    public void A_role_gated_related_rule_keeps_its_role_term_for_render_time_from_a_warm_cache()
    {
        var engine = Engine(Shop("'manager' in @user.roles"));
        var manager = CelFixtures.Alice with { Roles = new HashSet<Role> { Role.Authenticated, Role.Application("manager") } };

        var forManager = engine.Resolve("orders", DataOperation.List, manager).Using!;
        var forOther = engine.Resolve("orders", DataOperation.List, CelFixtures.Alice).Using!;

        forOther.Root.ShouldBeSameAs(forManager.Root);
        BoundScope(forOther).Using.ShouldNotBeNull().Root.ShouldBeOfType<CelBinary>().Operator.ShouldBe(CelBinaryOperator.In);
    }

    [Fact]
    public void The_update_rule_binds_both_of_its_slots()
    {
        var engine = Engine(Shop("true", ordersRules: [("list", "true"), ("update", OrdersByRegion)]));

        var decision = engine.Resolve("orders", DataOperation.Update, CelFixtures.Alice);

        BoundScope(decision.Using!).Denied.ShouldBeFalse();
        BoundScope(decision.WithCheck!).Denied.ShouldBeFalse();
    }

    [Fact]
    public void The_recursion_reads_the_catalog_once()
    {
        var catalog = Catalog(Shop("account_id.owner_id == @user.id"));
        var provider = Substitute.For<IPolicyCatalogProvider>();
        provider.Current.Returns(catalog);

        new PolicyEngine(provider).Resolve("orders", DataOperation.List, CelFixtures.Alice);

        _ = provider.Received(1).Current;
    }

    [Fact]
    public void A_reapplied_catalog_binds_afresh()
    {
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent("relations", Catalog(Shop("true")));
        var engine = new PolicyEngine(provider);
        BoundScope(engine.Resolve("orders", DataOperation.List, CelFixtures.Alice)).Denied.ShouldBeFalse();

        provider.SetCurrent("relations", Catalog(Shop(null)));

        BoundScope(engine.Resolve("orders", DataOperation.List, CelFixtures.Alice)).ShouldBeSameAs(RelationScope.Deny);
    }

    [Fact]
    public void Depth_beyond_the_limit_binds_deny_even_if_apply_was_bypassed()
    {
        var catalog = Catalog(Shop("true"));
        var depths = new List<int>();
        var binder = new RelationBinder((_, _, _, depth) =>
        {
            depths.Add(depth);
            return new PolicyEngine(new PolicyCatalogProvider()).Resolve(catalog, "customers", DataOperation.List, CelFixtures.Alice, depth);
        });
        var rule = Rule(catalog, "orders", DataOperation.List);

        var deep = binder.Bind(catalog, "orders", DataOperation.List, "using", rule, CelFixtures.Alice, RelationGraph.MaxDepth);
        var shallow = binder.Bind(catalog, "orders", DataOperation.List, "using", rule, CelFixtures.Alice, 0);

        BoundScope(deep!).ShouldBeSameAs(RelationScope.Deny);
        BoundScope(shallow!).Denied.ShouldBeFalse();
        depths.ShouldBe([1]);
    }

    [Fact]
    public void A_check_bound_through_the_binder_has_its_scope_set()
    {
        var catalog = Catalog(Shop("true"));
        var engine = new PolicyEngine(new PolicyCatalogProvider());
        var binder = new RelationBinder((snapshot, entity, context, depth) =>
            engine.Resolve(snapshot, entity, DataOperation.List, context, depth));
        var check = (CelRelation)Rule(catalog, "orders", DataOperation.List).Root;

        var bound = binder.Bind(catalog, "orders", "checks/0", check, CelFixtures.Alice);

        bound.Scope.ShouldNotBeNull().Denied.ShouldBeFalse();
    }

    private static RelationScope BoundScope(PolicyDecision decision) => BoundScope(decision.Using.ShouldNotBeNull());

    private static RelationScope BoundScope(CompiledExpression expression) => Scope(expression.Root);

    private static RelationScope Scope(CelNode root) => root.ShouldBeOfType<CelRelation>().Scope.ShouldNotBeNull();

    private static CompiledExpression Rule(PolicyCatalog catalog, string entity, DataOperation operation)
    {
        catalog.TryGetEntity(entity, out var policy).ShouldBeTrue();
        return policy.Operations[operation].Using.ShouldNotBeNull();
    }

    private static PolicyEngine Engine(string json)
    {
        var provider = new PolicyCatalogProvider();
        provider.SetCurrent("relations", Catalog(json));
        return new PolicyEngine(provider);
    }

    private static PolicyCatalog Catalog(string json)
    {
        var descriptor = AlvoDescriptor.Parse(json);
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), CelFixtures.Compiler);
    }

    /// <summary>
    /// <c>orders</c> (global) whose list reads its customer's region; <c>customers</c> with the given list rule, its own
    /// <c>account_id</c> ref to <c>accounts</c> (global, listable by all).
    /// </summary>
    private static string Shop(
        string? customersList, string customersTenancy = "global", (string Operation, string Source)[]? ordersRules = null)
    {
        var customers = Entity(
            [Field("owner_id", "uuid"), Field("region", "string"), Field("region_id", "uuid"), Ref("account_id", "accounts")],
            customersList is null ? [] : [("list", customersList)]);
        customers["tenancy"] = customersTenancy;
        var orders = Entity([Ref("customer_id", "customers")], ordersRules ?? [("list", OrdersByRegion)]);
        orders["tenancy"] = "global";
        var accounts = Entity([Field("owner_id", "uuid")], ("list", "true"));
        accounts["tenancy"] = "global";
        var project = JsonNode.Parse(Project(("accounts", accounts), ("customers", customers), ("orders", orders)))!.AsObject();
        project["tenancy"] = new JsonObject { ["enabled"] = true };
        project["auth"] = new JsonObject { ["roles"] = new JsonArray("manager") };
        return project.ToJsonString();
    }
}
