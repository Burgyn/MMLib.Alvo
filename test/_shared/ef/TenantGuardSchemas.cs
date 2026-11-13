using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The schemas the tenant-guard facts run on, on either engine: scoped entities whose references span
/// <c>(tenant_id, id)</c> (#161).
/// </summary>
public static class TenantGuardSchemas
{
    /// <summary>Scoped <c>work_orders</c> and a scoped <c>visits</c> whose <c>work_order_id</c> references it.</summary>
    /// <param name="onDelete">What deleting a work order does to its visits.</param>
    public static SchemaModel Pair(OnDelete onDelete) => new([WorkOrders(TenancyMode.Scoped), Child("visits", onDelete, required: true)]);

    /// <summary>
    /// The physical shape of a database created before the guard: the same tables, but <c>work_orders</c> carries no
    /// tenancy, hence no <c>(tenant_id, id)</c> alternate key and no guard.
    /// </summary>
    /// <param name="onDelete">What deleting a work order does to its visits.</param>
    public static SchemaModel Legacy(OnDelete onDelete) => new([WorkOrders(tenancy: null), Child("visits", onDelete, required: true)]);

    /// <summary>Scoped <c>work_orders</c> with a restrict, a cascade and a (nullable) setNull child.</summary>
    public static SchemaModel AllActions() => new([
        WorkOrders(TenancyMode.Scoped),
        Child("visits", OnDelete.Restrict, required: true),
        Child("tasks", OnDelete.Cascade, required: true),
        Child("notes", OnDelete.SetNull, required: false),
    ]);

    private static EntitySchema WorkOrders(TenancyMode? tenancy) => new()
    {
        Name = "work_orders",
        Tenancy = tenancy,
        Fields = tenancy is null
            ? [Id(), new FieldSchema { Name = "title", Type = FieldType.String, Required = true }]
            : [Id(), new FieldSchema { Name = "title", Type = FieldType.String, Required = true }, TenantId()],
    };

    private static EntitySchema Child(string name, OnDelete onDelete, bool required) => new()
    {
        Name = name,
        Tenancy = TenancyMode.Scoped,
        Fields = [
            Id(),
            new FieldSchema { Name = "work_order_id", Type = FieldType.Ref, Required = required, Nullable = !required, Reference = new RefSchema("work_orders", onDelete) },
            TenantId(),
        ],
    };

    private static FieldSchema Id() => new() { Name = "id", Type = FieldType.Uuid, Required = true };

    private static FieldSchema TenantId() => new() { Name = "tenant_id", Type = FieldType.Uuid, Required = true };
}
