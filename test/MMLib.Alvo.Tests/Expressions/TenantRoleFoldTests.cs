using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json.Nodes;

using static MMLib.Alvo.Tests.Expressions.TenantRoleCallers;
using static MMLib.Alvo.Tests.Rules.RelationDescriptors;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// <c>@user.roles</c> folds tenant roles only while every entity on the decision's path is scoped (spec §6.2), and both
/// backends agree: a tenant role counts in a scoped entity's rule, mask and condition and in a relation term from it into
/// a scoped entity — never in a global or untenanted entity's, never in anything reached from a global entity, never past
/// a global hop — and a warm relation cache never lends one caller's fold to another (S1 D1, D2).
/// </summary>
public sealed class TenantRoleFoldTests
{
    private const string DispatcherOnly = "'dispatcher' in @user.roles";
    private const string Prefix = "alvo_u";

    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();
    private static readonly string[] _folds = ["TRUE", "FALSE"];

    public static TheoryData<TenancyMode?, bool> Tenancies() => new()
    {
        { TenancyMode.Scoped, true },
        { TenancyMode.Global, false },
        { null, false },
    };

    [Theory]
    [MemberData(nameof(Tenancies))]
    public void A_role_literal_folds_by_the_entity_the_rule_belongs_to(TenancyMode? tenancy, bool admitted)
    {
        var rule = Compile(DispatcherOnly, CelProfile.Rule, tenancy);

        new SqlPredicateRenderer().Render(rule, Person, new TestFieldSqlRenderer(), "p").Sql.ShouldBe(admitted ? "TRUE" : "FALSE");
        CelInterpreter.EvaluatePredicate(rule, AlvoRecord.Empty, null, Person).ShouldBe(admitted);
    }

    [Theory]
    [MemberData(nameof(Tenancies))]
    public void A_field_operand_in_the_role_set_folds_by_the_same_rule(TenancyMode? tenancy, bool admitted)
    {
        var rule = Compile("status in @user.roles", CelProfile.Rule, tenancy);

        var predicate = new SqlPredicateRenderer().Render(rule, Person, new TestFieldSqlRenderer(), "p");
        var dispatcherRow = new AlvoRecord(new Dictionary<string, object?> { ["status"] = "dispatcher" });

        predicate.Parameters.Values.ShouldBe(admitted ? ["authenticated", "dispatcher"] : ["authenticated"], ignoreOrder: true);
        CelInterpreter.EvaluatePredicate(rule, dispatcherRow, null, Person).ShouldBe(admitted);
    }

    [Theory]
    [MemberData(nameof(Tenancies))]
    public void A_mask_and_a_condition_fold_by_the_same_rule(TenancyMode? tenancy, bool admitted)
    {
        var mask = Compile("!(" + DispatcherOnly + ")", CelProfile.Rule, tenancy);
        var condition = Compile(DispatcherOnly, CelProfile.Condition, tenancy);

        CelInterpreter.EvaluateMask(mask, Person).ShouldBe(!admitted);
        CelInterpreter.EvaluateCondition(condition, AlvoRecord.Empty, null, Person, changed: []).ShouldBe(admitted);
    }

    [Fact]
    public void A_relation_into_a_scoped_entity_sees_the_tenant_role_and_one_into_a_global_entity_does_not()
    {
        var world = RelationRenderWorld.Over(Descriptor());

        FoldInside(Read(world, "order_id.title == 'x'", Person), "orders").ShouldBe("TRUE");
        FoldInside(Read(world, "region_id.code == 'N'", Person), "regions").ShouldBe("FALSE");
        FoldInside(Read(world, "order_id.title == 'x'", ProjectDispatcher), "orders").ShouldBe("TRUE");
        FoldInside(Read(world, "region_id.code == 'N'", ProjectDispatcher), "regions").ShouldBe("TRUE");
    }

    [Fact]
    public void A_global_entity_s_relation_into_a_scoped_entity_never_sees_the_tenant_role()
    {
        var world = RelationRenderWorld.Over(Descriptor());

        Fold(world, "regions", "orders.exists(o, o.title == 'x')", Person, "orders").ShouldBe("FALSE");
        Fold(world, "regions", "jobs.exists(j, " + DispatcherOnly + ")", Person, "jobs").ShouldBe("FALSE");
        Fold(world, "regions", "orders.exists(o, o.title == 'x')", ProjectDispatcher, "orders").ShouldBe("TRUE");
        Fold(world, "regions", "jobs.exists(j, " + DispatcherOnly + ")", ProjectDispatcher, "jobs").ShouldBe("TRUE");
    }

    [Fact]
    public void A_scoped_entity_s_relation_into_a_scoped_entity_sees_the_tenant_role_in_its_body()
    {
        var world = RelationRenderWorld.Over(Descriptor());

        Fold(world, "orders", "jobs.exists(j, " + DispatcherOnly + ")", Person, "jobs").ShouldBe("TRUE");
        Fold(world, "orders", "jobs.exists(j, " + DispatcherOnly + ")", Plain, "jobs").ShouldBe("FALSE");
    }

    [Fact]
    public void A_tenant_role_never_counts_past_a_global_hop()
    {
        var world = RelationRenderWorld.Over(Descriptor());

        Fold(world, "jobs", "zone_id.code == 'N'", Person, "orders").ShouldBe("FALSE");
        Fold(world, "jobs", "zone_id.code == 'N'", ProjectDispatcher, "orders").ShouldBe("TRUE");
    }

    [Fact]
    public void A_write_probe_from_a_global_entity_never_sees_the_tenant_role()
    {
        var world = RelationRenderWorld.Over(Descriptor());
        var relation = (CelRelation)world.Bound("regions", "orders.exists(o, o.title == 'x')", Person).Root;
        var image = RelationSqlRenderTests.Image(("id", Guid.Parse("0b9f6a57-6a4d-4c1e-9a0e-6f1b1d1c0003")));

        var probe = _renderer.RenderRelation(
            relation, world.EntityOf("regions"), Person, new TestFieldSqlRenderer(), "alvo_q", RelationSqlRenderTests.ImageOf("test", image));

        FoldInside(probe.Sql, "orders").ShouldBe("FALSE");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_warm_cache_never_lends_one_caller_s_fold_to_another(bool personFirst)
    {
        const string Source = "order_id.title == 'x' && region_id.code == 'N'";
        var world = RelationRenderWorld.Over(Descriptor());
        AlvoContext[] order = personFirst ? [Person, Plain] : [Plain, Person];

        var bound = order.Select(caller => (Caller: caller, Tree: world.Bound("jobs", Source, caller))).ToList();
        var sql = bound.ToDictionary(pair => pair.Caller, pair => Render(pair.Tree, pair.Caller).Sql);

        bound[0].Tree.Root.ShouldBeSameAs(bound[1].Tree.Root);
        FoldInside(sql[Person], "orders").ShouldBe("TRUE");
        FoldInside(sql[Person], "regions").ShouldBe("FALSE");
        FoldInside(sql[Plain], "orders").ShouldBe("FALSE");
        FoldInside(sql[Plain], "regions").ShouldBe("FALSE");
    }

    private static CompiledExpression Compile(string source, CelProfile profile, TenancyMode? tenancy) =>
        CelFixtures.Compiler.Compile(source, profile, CelFixtures.Orders with { Tenancy = tenancy }).Expression!;

    private static string Read(RelationRenderWorld world, string source, AlvoContext caller) =>
        Render(world.Bound("jobs", source, caller), caller).Sql;

    private static SqlPredicate Render(CompiledExpression bound, AlvoContext caller, string outer = "jobs") =>
        _renderer.Render(bound, caller, new TestFieldSqlRenderer(), Prefix, RelationSqlRenderTests.ReadOf("test", outer));

    private static string Fold(RelationRenderWorld world, string outer, string source, AlvoContext caller, string table) =>
        FoldInside(Render(world.Bound(outer, source, caller), caller, outer).Sql, table);

    /// <summary>
    /// The related <c>list</c> rule's fold inside the <c>EXISTS</c> over <paramref name="table"/>: the one constant
    /// term, <c>(TRUE)</c> or <c>(FALSE)</c>, among the correlation, the body and the tenant scope.
    /// </summary>
    private static string FoldInside(string sql, string table)
    {
        var start = sql.IndexOf($"FROM \"{table}\"", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"no EXISTS over {table} in: {sql}");
        var end = sql.IndexOf("EXISTS", start, StringComparison.Ordinal);
        var exists = end < 0 ? sql[start..] : sql[start..end];
        string[] folds = [.. _folds.Where(fold => exists.Contains($"({fold})", StringComparison.Ordinal))];
        return folds.ShouldHaveSingleItem($"one constant fold inside the EXISTS over {table}: {exists}");
    }

    /// <summary>
    /// <c>regions</c> and <c>zones</c> are global, <c>orders</c> and <c>jobs</c> scoped. <c>regions</c> and <c>orders</c>
    /// list only for <c>dispatcher</c>; <c>zones</c> list through an order (a scoped entity behind a global hop);
    /// <c>jobs</c> list by a column, so no constant of its own competes with a fold inside its <c>EXISTS</c>.
    /// </summary>
    private static string Descriptor()
    {
        var project = JsonNode.Parse(Project(
            ("regions", Tenancy("global", Entity([Field("code", "string")], ("list", DispatcherOnly)))),
            ("zones", Tenancy("global", Entity([Field("code", "string")], ("list", "orders.exists(o, o.title == 'x')")))),
            ("orders", Tenancy("scoped", Entity(
                [Field("title", "string"), Ref("region_id", "regions"), Ref("zone_id", "zones")], ("list", DispatcherOnly)))),
            ("jobs", Tenancy("scoped", Entity(
                [Ref("region_id", "regions"), Ref("order_id", "orders"), Ref("zone_id", "zones"), Field("note", "string")],
                ("list", "note == 'open'"))))))!
            .AsObject();
        project["tenancy"] = new JsonObject { ["enabled"] = true };
        project["auth"] = new JsonObject { ["roles"] = new JsonArray("dispatcher") };
        return project.ToJsonString();
    }

    private static JsonObject Tenancy(string mode, JsonObject entity)
    {
        entity["tenancy"] = mode;
        return entity;
    }
}
