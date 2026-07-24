using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Abstractions.Tests.Schema;

public class SchemaModelTests
{
    [Fact]
    public void EntitySchema_defaults_are_physical_and_non_audited()
    {
        var e = new EntitySchema { Name = "vehicles", Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }] };
        e.Storage.ShouldBe(EntityStorage.Physical);
        e.Audit.ShouldBeFalse();
        e.SoftDelete.ShouldBeFalse();
        e.Tenancy.ShouldBeNull();
    }

    [Fact]
    public void FieldSchema_and_RefSchema_carry_their_facets()
    {
        var f = new FieldSchema { Name = "owner", Type = FieldType.Ref, Reference = new RefSchema("owners", OnDelete.Cascade) };
        f.Reference!.TargetEntity.ShouldBe("owners");
        f.Reference.OnDelete.ShouldBe(OnDelete.Cascade);
        var s = new FieldSchema { Name = "vin", Type = FieldType.String, MaxLength = 17, Required = true };
        s.Nullable.ShouldBeFalse();
        s.MaxLength.ShouldBe(17);
    }
}
