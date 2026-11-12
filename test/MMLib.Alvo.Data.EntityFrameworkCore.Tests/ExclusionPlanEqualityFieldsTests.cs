using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Issue 477: the migrator hands the dialect every author-declared equality field of the exclusions a plan adds, each at its
/// own pointer under the index's <c>fields</c>, so an engine that cannot compare one refuses it there.
/// </summary>
public sealed class ExclusionPlanEqualityFieldsTests
{
    [Fact]
    public void Each_declared_field_is_handed_over_at_its_position_with_its_type_and_the_managed_tenant_is_not()
    {
        var entity = new EntitySchema
        {
            Name = "bookings",
            Tenancy = TenancyMode.Scoped,
            Fields =
            [
                new FieldSchema { Name = "room", Type = FieldType.String },
                new FieldSchema { Name = "archived", Type = FieldType.Boolean },
            ],
            Indexes =
            [
                new IndexSchema(["room"], Unique: true),
                new IndexSchema(["room", "archived"], Unique: false) { Exclude = new ExclusionSchema("starts_at", "ends_at") },
            ],
        };
        var declared = new ExclusionPlan.Declared(
            entity, entity.Indexes[1], new ExclusionConstraintDefinition("bookings", "xc", ["tenant_id", "room", "archived"], "starts_at", "ends_at", FieldType.DateTime, null),
            new ExclusionConstraintSql([], [], SurvivesTableRebuild: true));

        ExclusionPlan.EqualityFields([declared]).ShouldBe(
        [
            new ExclusionEqualityField("/entities/bookings/indexes/1/fields/0", "room", FieldType.String),
            new ExclusionEqualityField("/entities/bookings/indexes/1/fields/1", "archived", FieldType.Boolean),
        ]);
    }
}
