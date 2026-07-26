using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The framework's one canonical data-path fixture: the entity every data-path suite reads, writes,
/// filters and snapshots, and the caller every rule is resolved against. Public and standalone, so a test
/// project reads it without deriving from — or taking a dependency on — any particular test suite.
/// </summary>
/// <remarks>
/// One definition, deliberately. The golden CEL→SQL snapshots, the adversarial suite, the differential
/// suite, the statement-composer tests and the binder tests all have to be talking about the same shape,
/// or a rule frozen against one entity is being replayed against another and the suites stop comparing
/// like with like. A per-project copy of a ten-field <see cref="EntitySchema"/> is how that drift starts.
/// </remarks>
public static class AlvoDataFixtures
{
    /// <summary>
    /// The shared fixture entity: one column of every field type Alvo maps, one nullable owner reference
    /// for row scoping, and one field a <c>hidden</c> rule can mask.
    /// </summary>
    public static EntitySchema Vehicle { get; } = new()
    {
        Name = "vehicle",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "plate", Type = FieldType.String, Required = true, MaxLength = 32 },
            new FieldSchema { Name = "status", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "secret_note", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "mileage", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "price", Type = FieldType.Decimal, Nullable = true, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "is_public", Type = FieldType.Boolean, Nullable = true },
            new FieldSchema { Name = "due_on", Type = FieldType.Date, Nullable = true },
            new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Nullable = true },
        ],
    };

    /// <summary>The caller every fixture rule is resolved against — a fixed, tenanted, admin-holding identity.</summary>
    public static AlvoContext Caller { get; } = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated, Role.Admin },
        Tenant = new TenantId(Guid.Parse("11111111-0000-0000-0000-000000000001")),
    };
}
