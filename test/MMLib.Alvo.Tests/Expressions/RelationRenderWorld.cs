using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json.Nodes;

using static MMLib.Alvo.Tests.Rules.RelationDescriptors;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A tenant-scoped world for relation renders: any rule source compiled over one of its entities, then bound for a
/// caller exactly as <c>PolicyEngine.Resolve</c> binds a stored rule.
/// </summary>
/// <remarks>
/// <c>customers</c> and <c>accounts</c> list by owner; <c>vendors</c> list through their account (a nested relation);
/// <c>project_members</c> by member; <c>ledgers</c> only for the <c>manager</c> role; <c>orders</c>, <c>projects</c> and
/// <c>employees</c> list freely.
/// </remarks>
internal sealed class RelationRenderWorld
{
    private readonly PolicyCatalog _catalog;
    private readonly SchemaModel _schema;
    private readonly RelationBinder _binder;

    private RelationRenderWorld(string json)
    {
        var descriptor = AlvoDescriptor.Parse(json);
        _schema = DescriptorToSchemaMapper.Map(descriptor);
        _catalog = PolicyCatalog.Build(descriptor, _schema, CelFixtures.Compiler);
        Relations = RelationCatalogBuilder.From(descriptor, _schema);
        var engine = new PolicyEngine(new PolicyCatalogProvider());
        _binder = new RelationBinder((snapshot, entity, context, depth) =>
            engine.Resolve(snapshot, entity, DataOperation.List, context, depth));
    }

    internal CelRelationCatalog Relations { get; }

    internal static RelationRenderWorld Instance { get; } = new(Descriptor());

    internal static AlvoContext Manager { get; } =
        CelFixtures.Alice with { Roles = new HashSet<Role> { Role.Authenticated, Role.Application("manager") } };

    internal EntitySchema EntityOf(string name) => _schema.Entities.Single(entity => entity.Name == name);

    /// <summary>The rule compiled over <paramref name="entity"/>, unbound.</summary>
    internal CompiledExpression Compile(string entity, string source)
    {
        var result = CelFixtures.Compiler.Compile(source, CelProfile.Rule, EntityOf(entity), Relations);
        return result.IsSuccess
            ? result.Expression!
            : throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    /// <summary>The rule compiled over <paramref name="entity"/> and bound for <paramref name="caller"/>.</summary>
    internal CompiledExpression Bound(string entity, string source, AlvoContext caller) =>
        _binder.Bind(_catalog, entity, DataOperation.List, "render:" + source, Compile(entity, source), caller, 0)!;

    private static string Descriptor()
    {
        var project = JsonNode.Parse(Project(
            ("accounts", Scoped(Entity([Field("owner_id", "uuid")], ("list", "owner_id == @user.id")))),
            ("customers", Scoped(Entity(
                [Ref("account_id", "accounts"), Field("owner_id", "uuid"), Field("region", "string"), Field("active", "boolean"),
                 Money("credit_limit"), Field("since", "datetime"), Field("opened_on", "date"), Field("note", "string")],
                ("list", "owner_id == @user.id")))),
            ("vendors", Scoped(Entity(
                [Ref("account_id", "accounts"), Field("region", "string")], ("list", "account_id.owner_id == @user.id")))),
            ("ledgers", Scoped(Entity([Field("region", "string")], ("list", "'manager' in @user.roles")))),
            ("orders", Scoped(Entity(
                [Ref("customer_id", "customers"), Ref("vendor_id", "vendors"), Ref("ledger_id", "ledgers"),
                 Field("region", "string"), Field("note", "string"), Money("amount"), Field("placed_at", "datetime"),
                 Field("due_on", "date"), Field("urgent", "boolean")],
                ("list", "true")))),
            ("projects", Scoped(Entity([Field("archived", "boolean"), Field("note", "string")], ("list", "true")))),
            ("project_members", Scoped(Entity(
                [Ref("project_id", "projects"), Field("user_id", "uuid"), Field("note", "string")],
                ("list", "user_id == @user.id")))),
            ("employees", Scoped(Entity(
                [Ref("manager_id", "employees"), Field("region", "string")], ("list", "'authenticated' in @user.roles"))))))!
            .AsObject();
        project["tenancy"] = new JsonObject { ["enabled"] = true };
        project["auth"] = new JsonObject { ["roles"] = new JsonArray("manager") };
        return project.ToJsonString();
    }

    private static JsonObject Scoped(JsonObject entity)
    {
        entity["tenancy"] = "scoped";
        return entity;
    }

    private static (string, JsonObject) Money(string name) =>
        (name, new JsonObject { ["type"] = "decimal", ["precision"] = 12, ["scale"] = 2 });
}
