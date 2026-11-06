using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A small related schema for relation-term compiles: <c>orders.customer_id</c> refs <c>customers</c>,
/// <c>project_members.project_id</c> refs <c>projects</c>, <c>follows</c> refs <c>people</c> twice, and
/// <c>orders</c> holds a ref to <c>users</c> and one to the dynamic <c>notes</c>, which a rule may not read.
/// </summary>
internal static class RelationFixtures
{
    internal static EntitySchema Customers { get; } = Entity(
        "customers",
        Field("owner_id", FieldType.Uuid),
        Field("region", FieldType.String),
        Field("active", FieldType.Boolean),
        Field("note", FieldType.String, nullable: true),
        new FieldSchema { Name = "tier", Type = FieldType.Enum, EnumValues = ["gold", "silver"] });

    internal static EntitySchema Orders { get; } = Entity(
        "orders",
        Ref("customer_id", "customers"),
        Ref("assignee_id", "users"),
        Ref("note_id", "notes"),
        Ref("project_id", "projects"),
        Field("region", FieldType.String),
        Field("note", FieldType.String, nullable: true),
        Field("title", FieldType.String));

    internal static EntitySchema Projects { get; } = Entity(
        "projects",
        Field("owner_id", FieldType.Uuid),
        Field("note", FieldType.String, nullable: true));

    internal static EntitySchema ProjectMembers { get; } = Entity(
        "project_members",
        Ref("project_id", "projects"),
        Field("user_id", FieldType.Uuid),
        Field("note", FieldType.String, nullable: true));

    internal static EntitySchema People { get; } = Entity("people", Field("name", FieldType.String));

    internal static EntitySchema Follows { get; } = Entity(
        "follows",
        Ref("follower", "people"),
        Ref("followee", "people"));

    internal static CelRelationCatalog Catalog { get; } = BuildCatalog();

    internal static CelCompilationResult Compile(string source, EntitySchema entity) =>
        ((ICelCompiler)new CelCompiler()).Compile(source, CelProfile.Rule, entity, Catalog);

    internal static CompiledExpression CompileRule(string source, EntitySchema entity)
    {
        var result = Compile(source, entity);
        return result.IsSuccess
            ? result.Expression!
            : throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    private static CelRelationCatalog BuildCatalog() => RelationCatalogBuilder.From(
        [Customers, Orders, Projects, ProjectMembers, People, Follows],
        new Dictionary<string, string>(StringComparer.Ordinal) { ["notes"] = RelationRefusals.Dynamic });

    private static EntitySchema Entity(string name, params FieldSchema[] fields) => new()
    {
        Name = name,
        Fields = [Field("id", FieldType.Uuid), .. fields],
    };

    private static FieldSchema Field(string name, FieldType type, bool nullable = false) =>
        new() { Name = name, Type = type, Nullable = nullable };

    private static FieldSchema Ref(string name, string target) =>
        new() { Name = name, Type = FieldType.Ref, Nullable = true, Reference = new RefSchema(target, OnDelete.Restrict) };
}
