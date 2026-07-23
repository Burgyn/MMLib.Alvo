using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Schema;

public class SchemaRegistryTests
{
    [Fact]
    public void Returns_the_seeded_model()
    {
        var model = new SchemaModel([new EntitySchema { Name = "v", Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }] }]);
#pragma warning disable CA1859
        ISchemaRegistry reg = new SchemaRegistry(model);
#pragma warning restore CA1859
        reg.GetSchema().ShouldBe(model);
    }
}
