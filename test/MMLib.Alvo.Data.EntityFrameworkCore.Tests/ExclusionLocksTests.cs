using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// HX-12 D3's key parity (review I2): a create's candidate and an update's merged post-image of one resource hash to one
/// lock key for every admitted equality type, however the value was read — or the two writers would not queue.
/// </summary>
public sealed class ExclusionLocksTests
{
    private static readonly Guid _tenant = Guid.NewGuid();
    private static readonly Guid _room = Guid.NewGuid();

    public static TheoryData<FieldType, object, object> SameValueReadTwoWays => new()
    {
        { FieldType.String, "r1", "r1" },
        { FieldType.Integer, 5L, 5 },
        { FieldType.Decimal, 1.5m, 1.50m },
        { FieldType.Decimal, 1m, 1.00m },
        { FieldType.Boolean, true, true },
        { FieldType.Uuid, _room, new Guid(_room.ToString()) },
        { FieldType.Date, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 1) },
        { FieldType.Enum, "held", "held" },
    };

    [Theory]
    [MemberData(nameof(SameValueReadTwoWays))]
    public void A_create_and_an_update_of_one_resource_take_one_key(FieldType type, object created, object stored)
    {
        var entity = Bookings(type);

        var createKey = ExclusionLocks.KeyFor(entity, entity.Indexes[0], Row(created, _tenant));
        var updateKey = ExclusionLocks.KeyFor(entity, entity.Indexes[0], Row(stored, new Guid(_tenant.ToString("N"))));

        updateKey.ShouldBe(createKey);
    }

    [Fact]
    public void Another_resource_or_another_tenant_takes_another_key()
    {
        var entity = Bookings(FieldType.String);

        var key = ExclusionLocks.KeyFor(entity, entity.Indexes[0], Row("r1", _tenant));

        ExclusionLocks.KeyFor(entity, entity.Indexes[0], Row("r2", _tenant)).ShouldNotBe(key);
        ExclusionLocks.KeyFor(entity, entity.Indexes[0], Row("r1", Guid.NewGuid())).ShouldNotBe(key);
    }

    private static Func<string, object?> Row(object resource, Guid tenant)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant_id"] = tenant, ["resource"] = resource };
        return column => row.GetValueOrDefault(column);
    }

    private static EntitySchema Bookings(FieldType type) => new()
    {
        Name = "bookings",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "resource", Type = type, Required = true, EnumValues = type == FieldType.Enum ? ["held"] : null },
            new FieldSchema { Name = "starts_at", Type = FieldType.DateTime, Required = true },
            new FieldSchema { Name = "ends_at", Type = FieldType.DateTime, Required = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true },
        ],
        Indexes = [new IndexSchema(["resource"], Unique: false) { Exclude = new ExclusionSchema("starts_at", "ends_at") }],
    };
}
