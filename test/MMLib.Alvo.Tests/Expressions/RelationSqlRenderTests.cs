using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

using System.Text.RegularExpressions;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A bound relation renders as one correlated <c>EXISTS</c> over the related table under its own alias, with the related
/// <c>list</c> rule and tenant scope ANDed inside; the outer row is qualified by its table in a read and bound from the
/// post-image in a write probe; every nested scope shares one parameter bag; and an unbound relation never renders.
/// </summary>
public sealed partial class RelationSqlRenderTests
{
    private const string Prefix = "alvo_u";

    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();
    private static readonly RelationRenderWorld _world = RelationRenderWorld.Instance;
    private static readonly Guid _customer = Guid.Parse("0b9f6a57-6a4d-4c1e-9a0e-6f1b1d1c0001");
    private static readonly Guid _project = Guid.Parse("0b9f6a57-6a4d-4c1e-9a0e-6f1b1d1c0002");

    public static TheoryData<string> Dialects() => ["test", "tsql"];

    [Theory]
    [MemberData(nameof(Dialects))]
    public Task A_forward_relation_renders_one_exists_with_the_related_using_and_tenant_scope(string dialect) =>
        Verify(Snapshot(Read(dialect, "orders", "customer_id.region == 'eu'"))).UseParameters(dialect);

    [Theory]
    [MemberData(nameof(Dialects))]
    public Task A_reverse_relation_correlates_the_child_s_via_with_the_outer_id(string dialect) =>
        Verify(Snapshot(Read(dialect, "projects", "project_members.exists(m, m.user_id == @user.id)"))).UseParameters(dialect);

    [Fact]
    public void An_outer_reference_inside_the_body_is_qualified_by_the_outer_table()
    {
        var sql = Read("test", "orders", "customer_id.region == region").Sql;

        sql.ShouldContain("COALESCE(alvo_r1.\"region\" = \"orders\".\"region\", FALSE)");
        sql.ShouldContain("alvo_r1.\"id\" = \"orders\".\"customer_id\"");
    }

    public static TheoryData<string, AlvoContext> DeniedCallers() => new()
    {
        { "anonymous in a tenant", AlvoContext.Anonymous with { Tenant = CelFixtures.Alice.Tenant } },
        { "tenantless", CelFixtures.TenantlessAlice },
    };

    [Theory]
    [MemberData(nameof(DeniedCallers))]
    public void A_denied_scope_renders_false_and_reads_no_table(string why, AlvoContext caller)
    {
        _ = why;
        var bound = _world.Bound("orders", "customer_id.region == 'eu'", caller);

        var predicate = _renderer.Render(bound, caller, new TestFieldSqlRenderer(), Prefix, ReadOf("test", "orders"));

        predicate.Sql.ShouldBe("FALSE");
        predicate.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public Task Nested_scopes_share_the_parent_bag_and_never_reuse_a_name()
    {
        var predicate = Read("test", "orders", "vendor_id.region == 'eu' && customer_id.owner_id == @user.id");

        var markers = Markers().Matches(predicate.Sql).Select(match => match.Groups[1].Value).ToList();
        markers.Distinct().Count().ShouldBe(markers.Count);
        markers.ToHashSet().SetEquals(predicate.Parameters.Keys).ShouldBeTrue();
        predicate.Parameters.Keys.ShouldAllBe(name => name.StartsWith(Prefix, StringComparison.Ordinal));
        Aliases().Matches(predicate.Sql).Select(match => match.Value).ShouldBe(["AS alvo_r1", "AS alvo_r2", "AS alvo_r3"]);
        return Verify(Snapshot(predicate));
    }

    [Fact]
    public Task An_image_outer_binds_every_outer_value_as_a_parameter()
    {
        var row = Image(("customer_id", _customer.ToString()), ("region", "e'u;--"), ("amount", 12.5d));
        var bound = _world.Bound("orders", "customer_id.region == region && customer_id.credit_limit >= amount", CelFixtures.Alice);
        var terms = Relations(bound.Root).Select(relation => Probe("test", relation, "orders", row)).ToList();

        terms[0].Parameters.Values.ShouldContain("e'u;--");
        terms[0].Sql.ShouldNotContain("e'u");
        terms[1].Parameters.Values.ShouldContain(12.5m);
        terms.ShouldAllBe(term => term.Parameters.Values.Contains(_customer));
        return Verify(terms.Select(Snapshot));
    }

    [Fact]
    public void A_null_outer_key_in_an_image_reads_no_table()
    {
        var bound = _world.Bound("orders", "customer_id.region == 'eu'", CelFixtures.Alice);

        var probe = Probe("test", (CelRelation)bound.Root, "orders", Image(("customer_id", null)));

        probe.Sql.ShouldBe("FALSE");
    }

    [Fact]
    public void An_unbound_relation_is_refused()
    {
        var unbound = _world.Compile("orders", "customer_id.region == 'eu'");

        Should.Throw<InvalidOperationException>(() =>
            _renderer.Render(unbound, CelFixtures.Alice, new TestFieldSqlRenderer(), Prefix, ReadOf("test", "orders")))
            .Message.ShouldContain("not bound");
        Should.Throw<InvalidOperationException>(() => Probe("test", (CelRelation)unbound.Root, "orders", Image()));
    }

    [Fact]
    public void A_role_gated_related_rule_renders_per_caller_from_a_warm_cache()
    {
        const string Source = "ledger_id.region == 'eu'";
        var forManager = _world.Bound("orders", Source, RelationRenderWorld.Manager);
        var forOther = _world.Bound("orders", Source, CelFixtures.Alice);

        forOther.Root.ShouldBeSameAs(forManager.Root);
        Render("test", forManager, RelationRenderWorld.Manager, ReadOf("test", "orders")).Sql.ShouldContain("AND (TRUE) AND");
        Render("test", forOther, CelFixtures.Alice, ReadOf("test", "orders")).Sql.ShouldContain("AND (FALSE) AND");
    }

    internal static SqlPredicate Read(string dialect, string entity, string source)
    {
        var bound = _world.Bound(entity, source, CelFixtures.Alice);
        return Render(dialect, bound, CelFixtures.Alice, ReadOf(dialect, entity));
    }

    internal static SqlPredicate Render(string dialect, CompiledExpression bound, AlvoContext caller, RelationSql relations) =>
        _renderer.Render(bound, caller, Fields(dialect), Prefix, relations);

    internal static SqlPredicate Probe(string dialect, CelRelation relation, string outer, AlvoRecord row) =>
        _renderer.RenderRelation(relation, _world.EntityOf(outer), CelFixtures.Alice, Fields(dialect), "alvo_q", ImageOf(dialect, row));

    internal static RelationSql ReadOf(string dialect, string entity) => new(Table(dialect), new RowSource.Columns(Quote(dialect, entity)));

    internal static RelationSql ImageOf(string dialect, AlvoRecord row) => new(Table(dialect), new RowSource.Image(row));

    internal static IEnumerable<CelRelation> Relations(CelNode root) => root switch
    {
        CelRelation relation => [relation],
        CelBinary binary => Relations(binary.Left).Concat(Relations(binary.Right)),
        _ => [],
    };

    internal static AlvoRecord Image(params (string Field, object? Value)[] values) =>
        new(values.ToDictionary(pair => pair.Field, pair => pair.Value, StringComparer.Ordinal));

    internal static IFieldSqlRenderer Fields(string dialect) =>
        dialect == "tsql" ? new TSqlFieldSqlRenderer() : new TestFieldSqlRenderer();

    internal static object Snapshot(SqlPredicate predicate) => new
    {
        predicate.Sql,
        Parameters = predicate.Parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new { pair.Key, Type = pair.Value?.GetType().Name, Value = Scrub(pair.Value) }),
    };

    private static Func<MMLib.Alvo.Schema.EntitySchema, string> Table(string dialect) => entity => Quote(dialect, entity.Name);

    private static string Quote(string dialect, string name) => dialect == "tsql" ? $"[{name}]" : $"\"{name}\"";

    private static object? Scrub(object? value) => value switch
    {
        Guid guid when guid == CelFixtures.Alice.User.Value => "{alice-user-id}",
        Guid guid when guid == CelFixtures.AcmeTenant.Value => "{acme-tenant-id}",
        _ => value,
    };

    [GeneratedRegex(@"@(alvo_\w+)")]
    private static partial Regex Markers();

    [GeneratedRegex(@"AS alvo_r\d+")]
    private static partial Regex Aliases();
}
