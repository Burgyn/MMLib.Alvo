using MMLib.Alvo.Schema;
using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The framework's one canonical data-path fixture: the entity every data-path suite reads, writes,
/// filters and snapshots, and the caller every rule is resolved against. Public and standalone, so a test
/// project reads it without deriving from — or taking a dependency on — any particular test suite.
/// </summary>
/// <remarks>
/// <para>
/// Both collections are frozen. This type is public and shipped, so an array behind an
/// <c>IReadOnlyList</c> and a <see cref="HashSet{T}"/> behind an <c>IReadOnlySet</c> are one cast away from
/// being mutated — and there is exactly one instance of each, read by every data-path suite in the framework.
/// </para>
/// <para>
/// One definition, deliberately. The golden CEL→SQL snapshots, the adversarial suite, the differential
/// suite, the statement-composer tests and the binder tests all have to be talking about the same shape,
/// or a rule frozen against one entity is being replayed against another and the suites stop comparing
/// like with like. A per-project copy of a ten-field <see cref="EntitySchema"/> is how that drift starts.
/// </para>
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
        Fields = FrozenFields(
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
        ]),
    };

    /// <summary>
    /// Freezes the field list, so the one shared instance cannot be mutated by a consumer that casts the
    /// <c>IReadOnlyList</c> back to the array behind it.
    /// </summary>
    private static ReadOnlyCollection<FieldSchema> FrozenFields(params FieldSchema[] fields) => fields.AsReadOnly();

    /// <summary>The caller every fixture rule is resolved against — a fixed, tenanted, admin-holding identity.</summary>
    public static AlvoContext Caller { get; } = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated, Role.Admin }.ToFrozenSet(),
        Tenant = new TenantId(Guid.Parse("11111111-0000-0000-0000-000000000001")),
    };
}
