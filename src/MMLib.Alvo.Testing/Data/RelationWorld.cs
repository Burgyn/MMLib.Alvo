using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using Xunit;
using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The relation-rule world, tenancy on: <c>customers</c> listed by owner; <c>orders</c> listed through an active customer
/// and created through an owned one; <c>quotes</c> created under the customer's credit limit and start; <c>projects</c>
/// listed by membership; <c>regions</c> (global) listed through a managed <c>site</c>; <c>employees</c> read through
/// their manager's region. Every create rule also admits the built-in <c>admin</c> role, which seeds rows in their own tenant.
/// </summary>
/// <param name="data">The port the world is seeded through.</param>
internal sealed class RelationWorld(IAlvoData data)
{
    internal const string Customers = "customers";
    internal const string Orders = "orders";
    internal const string Quotes = "quotes";
    internal const string Projects = "projects";
    internal const string ProjectMembers = "project_members";
    internal const string Regions = "regions";
    internal const string Sites = "sites";
    internal const string Employees = "employees";

    private const string Seeding = "'admin' in @user.roles";

    internal static DateTimeOffset Since { get; } = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    internal TenantId Tenant1 { get; } = TenantId.New();

    internal TenantId Tenant2 { get; } = TenantId.New();

    /// <summary>A caller in <see cref="Tenant1"/>.</summary>
    internal AlvoContext A => _a ??= Caller(Guid.NewGuid(), Tenant1);

    /// <summary>Another caller in <see cref="Tenant1"/>.</summary>
    internal AlvoContext B => _b ??= Caller(Guid.NewGuid(), Tenant1);

    private AlvoContext? _a;
    private AlvoContext? _b;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static AlvoContext Caller(Guid user, TenantId tenant, params Role[] roles) => new()
    {
        User = new UserId(user),
        Tenant = tenant,
        Roles = new HashSet<Role>([Role.Authenticated, .. roles]),
    };

    internal Dictionary<string, object?> Order(Guid? customer, string note = "order") =>
        Row(("customer_id", customer), ("note", note));

    internal Dictionary<string, object?> Quote(Guid customer, decimal amount, DateTimeOffset placedAt) =>
        Row(("customer_id", customer), ("amount", amount), ("placed_at", placedAt));

    internal Task<Guid> SeedCustomerAsync(AlvoContext owner, bool active, decimal creditLimit = 0m) =>
        SeedAsync(Customers, owner.Tenant!.Value, ("owner_id", owner.User.Value), ("active", active), ("region", "eu"),
            ("credit_limit", creditLimit), ("since", Since));

    internal async Task<Guid> SeedOrderAsync(AlvoContext owner, bool active) =>
        await SeedAsync(Orders, owner.Tenant!.Value, ("customer_id", await SeedCustomerAsync(owner, active)), ("note", "order"));

    internal Task<Guid> SeedAsync(string entity, TenantId tenant, params (string Field, object? Value)[] values) =>
        IdOf(data.CreateAsync(entity, Row([.. values, ("tenant_id", tenant.Value)]), Seeder(tenant), cancellationToken: Ct));

    internal Task<Guid> SeedGlobalAsync(string entity, params (string Field, object? Value)[] values) =>
        IdOf(data.CreateAsync(entity, Row(values), Seeder(Tenant1), cancellationToken: Ct));

    internal Task<AlvoRecord> UpdateAsSeederAsync(string entity, Guid id, TenantId tenant, string field, object? value) =>
        data.UpdateAsync(entity, id, Row((field, value)), Seeder(tenant), cancellationToken: Ct);

    private static AlvoContext Seeder(TenantId tenant) => Caller(Guid.NewGuid(), tenant, Role.Admin);

    private Dictionary<string, object?> Row(params (string Field, object? Value)[] values)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in values)
        {
            row[field] = value;
        }

        row.TryAdd("tenant_id", A.Tenant!.Value.Value);
        return row;
    }

    private static async Task<Guid> IdOf(Task<AlvoRecord> created) => (Guid)(await created)["id"]!;

    internal static AlvoDescriptor Descriptor() => AlvoDescriptor.Parse(DescriptorJson);

    internal static SchemaModel Schema() => new(
    [
        Scoped(Customers, Uuid("owner_id"), Bool("active"), Text("region"), Money("credit_limit"), Instant("since")),
        Scoped(Orders, Ref("customer_id", Customers), Text("note")),
        Scoped(Quotes, Ref("customer_id", Customers), Money("amount"), Instant("placed_at")),
        Scoped(Projects, Text("name")),
        Scoped(ProjectMembers, Ref("project_id", Projects), Uuid("user_id")),
        Global(Regions, Text("name")),
        Scoped(Sites, Ref("region_id", Regions), Uuid("manager_id")),
        Scoped(Employees, Ref("manager_id", Employees), Text("region")),
    ]);

    private const string DescriptorJson = $$"""
        {
          "apiVersion": "alvo.dev/v1", "name": "relation-rules",
          "tenancy": { "enabled": true },
          "entities": {
            "customers": { "tenancy": "scoped",
              "fields": { "owner_id": { "type": "uuid" }, "active": { "type": "boolean" }, "region": { "type": "string" },
                          "credit_limit": { "type": "decimal", "precision": 12, "scale": 2 }, "since": { "type": "datetime" } },
              "rules": { "list": "owner_id == @user.id", "get": "owner_id == @user.id", "create": "{{Seeding}}", "update": "{{Seeding}}" } },
            "orders": { "tenancy": "scoped",
              "fields": { "customer_id": { "type": "ref", "entity": "customers" }, "note": { "type": "string" } },
              "rules": { "list": "customer_id.active == true", "get": "customer_id.active == true",
                         "update": "customer_id.active == true", "delete": "customer_id.active == true",
                         "create": "{{Seeding}} || customer_id.owner_id == @user.id" } },
            "quotes": { "tenancy": "scoped",
              "fields": { "customer_id": { "type": "ref", "entity": "customers" },
                          "amount": { "type": "decimal", "precision": 12, "scale": 2 }, "placed_at": { "type": "datetime" } },
              "rules": { "list": "true", "get": "true",
                         "create": "customer_id.credit_limit >= amount && customer_id.since <= placed_at" } },
            "projects": { "tenancy": "scoped",
              "fields": { "name": { "type": "string" } },
              "rules": { "list": "project_members.exists(m, m.user_id == @user.id)",
                         "get": "project_members.exists(m, m.user_id == @user.id)",
                         "update": "project_members.exists(m, m.user_id == @user.id)", "create": "{{Seeding}}" } },
            "project_members": { "tenancy": "scoped",
              "fields": { "project_id": { "type": "ref", "entity": "projects" }, "user_id": { "type": "uuid" } },
              "rules": { "list": "user_id == @user.id", "create": "'authenticated' in @user.roles" } },
            "regions": { "tenancy": "global",
              "fields": { "name": { "type": "string" } },
              "rules": { "list": "sites.exists(s, s.manager_id == @user.id)", "get": "sites.exists(s, s.manager_id == @user.id)",
                         "create": "{{Seeding}}" } },
            "sites": { "tenancy": "scoped",
              "fields": { "region_id": { "type": "ref", "entity": "regions" }, "manager_id": { "type": "uuid" } },
              "rules": { "list": "manager_id == @user.id", "create": "{{Seeding}}", "update": "{{Seeding}}" } },
            "employees": { "tenancy": "scoped",
              "fields": { "manager_id": { "type": "ref", "entity": "employees" }, "region": { "type": "string" } },
              "rules": { "list": "'authenticated' in @user.roles", "get": "manager_id.region == 'eu'", "create": "{{Seeding}}" } }
          }
        }
        """;

    private static EntitySchema Scoped(string name, params FieldSchema[] fields) => new()
    {
        Name = name,
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true },
            .. fields,
        ],
    };

    private static EntitySchema Global(string name, params FieldSchema[] fields) => new()
    {
        Name = name,
        Tenancy = TenancyMode.Global,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }, .. fields],
    };

    private static FieldSchema Uuid(string name) => new() { Name = name, Type = FieldType.Uuid, Nullable = true };

    private static FieldSchema Bool(string name) => new() { Name = name, Type = FieldType.Boolean, Nullable = true };

    private static FieldSchema Text(string name) => new() { Name = name, Type = FieldType.String, Nullable = true };

    private static FieldSchema Instant(string name) => new() { Name = name, Type = FieldType.DateTime, Nullable = true };

    private static FieldSchema Money(string name) =>
        new() { Name = name, Type = FieldType.Decimal, Precision = 12, Scale = 2, Nullable = true };

    private static FieldSchema Ref(string name, string target) =>
        new() { Name = name, Type = FieldType.Ref, Nullable = true, Reference = new RefSchema(target, OnDelete.Restrict) };
}
