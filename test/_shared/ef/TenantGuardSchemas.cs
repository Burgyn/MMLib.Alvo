using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The schemas the tenant-guard facts run on, on either engine: scoped entities whose references span
/// <c>(tenant_id, id)</c> (#161).
/// </summary>
public static class TenantGuardSchemas
{
    /// <summary>
    /// Scoped <c>work_orders</c> and a scoped <c>visits</c> whose <c>work_order_id</c> references it — required, except
    /// under <c>setNull</c>, which has to be able to clear it.
    /// </summary>
    /// <param name="onDelete">What deleting a work order does to its visits.</param>
    public static SchemaModel Pair(OnDelete onDelete) =>
        new([WorkOrders(TenancyMode.Scoped), Child("visits", onDelete, required: onDelete != OnDelete.SetNull)]);

    /// <summary>The same two scoped tables before the reference is added: <c>visits</c> has no <c>work_order_id</c>.</summary>
    public static SchemaModel Unreferenced() => new([
        WorkOrders(TenancyMode.Scoped),
        new EntitySchema { Name = "visits", Tenancy = TenancyMode.Scoped, Fields = [Id(), TenantId()] },
    ]);

    /// <summary>
    /// <see cref="Pair"/> under <c>restrict</c> with <paramref name="entity"/> — <c>work_orders</c> or <c>visits</c> — made
    /// global, so it has no <c>tenant_id</c> and the reference no guard.
    /// </summary>
    /// <param name="entity">The end of the reference whose tenancy flips.</param>
    public static SchemaModel Flipped(string entity) => entity == "work_orders"
        ? new([WorkOrders(TenancyMode.Global), Child("visits", OnDelete.Restrict, required: true)])
        : new([
            WorkOrders(TenancyMode.Scoped),
            new EntitySchema { Name = "visits", Tenancy = TenancyMode.Global, Fields = [Id(), Ref("work_order_id", OnDelete.Restrict, required: true)] },
        ]);

    /// <summary>
    /// <see cref="Pair"/> under <c>restrict</c> after the declared renames <paramref name="renames"/> names: <c>entity</c> renames
    /// <c>work_orders</c> to <c>jobs</c>, <c>field</c> renames <c>visits.work_order_id</c> to <c>job_id</c>, <c>both</c> does both.
    /// </summary>
    /// <param name="renames"><c>entity</c>, <c>field</c> or <c>both</c>.</param>
    public static SchemaModel Renamed(string renames)
    {
        var target = renames == "field" ? WorkOrders(TenancyMode.Scoped) : WorkOrders(TenancyMode.Scoped) with { Name = "jobs", RenamedFrom = "work_orders" };
        var reference = renames == "entity"
            ? Ref("work_order_id", OnDelete.Restrict, required: true, target: target.Name)
            : Ref("job_id", OnDelete.Restrict, required: true, target: target.Name) with { RenamedFrom = "work_order_id" };

        return new([target, new EntitySchema { Name = "visits", Tenancy = TenancyMode.Scoped, Fields = [Id(), reference, TenantId()] }]);
    }

    /// <summary><see cref="Renamed"/> with the reference dropped, which drops its guard and the target's alternate key.</summary>
    /// <param name="renames"><c>entity</c>, <c>field</c> or <c>both</c>.</param>
    public static SchemaModel RenamedWithoutTheReference(string renames) => new([
        WorkOrders(TenancyMode.Scoped) with { Name = renames == "field" ? "work_orders" : "jobs" },
        new EntitySchema { Name = "visits", Tenancy = TenancyMode.Scoped, Fields = [Id(), TenantId()] },
    ]);

    /// <summary>Scoped <c>work_orders</c> with a restrict, a cascade and a (nullable) setNull child.</summary>
    public static SchemaModel AllActions() => new([
        WorkOrders(TenancyMode.Scoped),
        Child("visits", OnDelete.Restrict, required: true),
        Child("tasks", OnDelete.Cascade, required: true),
        Child("notes", OnDelete.SetNull, required: false),
    ]);

    private static EntitySchema WorkOrders(TenancyMode tenancy) => new()
    {
        Name = "work_orders",
        Tenancy = tenancy,
        Fields = tenancy == TenancyMode.Scoped
            ? [Id(), new FieldSchema { Name = "title", Type = FieldType.String, Required = true }, TenantId()]
            : [Id(), new FieldSchema { Name = "title", Type = FieldType.String, Required = true }],
    };

    private static EntitySchema Child(string name, OnDelete onDelete, bool required) => new()
    {
        Name = name,
        Tenancy = TenancyMode.Scoped,
        Fields = [Id(), Ref("work_order_id", onDelete, required), TenantId()],
    };

    private static FieldSchema Ref(string name, OnDelete onDelete, bool required, string target = "work_orders") => new()
    {
        Name = name,
        Type = FieldType.Ref,
        Required = required,
        Nullable = !required,
        Reference = new RefSchema(target, onDelete),
    };

    private static FieldSchema Id() => new() { Name = "id", Type = FieldType.Uuid, Required = true };

    private static FieldSchema TenantId() => new() { Name = "tenant_id", Type = FieldType.Uuid, Required = true };
}
