using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Descriptor;

public class DescriptorToSchemaMapperTests
{
    private static SchemaModel Map(string file)
        => DescriptorToSchemaMapper.Map(DescriptorParser.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", file))));

    [Fact]
    public void Injects_id_when_absent()
    {
        var m = Map("simple-tasks/tasks.alvo.json");
        var tasks = m.Entities.Single(e => e.Name == "tasks");
        tasks.Fields.ShouldContain(f => f.Name == "id" && f.Type == FieldType.Uuid);
    }

    [Fact]
    public void Audit_entity_gets_managed_audit_columns()
    {
        var m = Map("simple-tasks/tasks.alvo.json");
        var tasks = m.Entities.Single(e => e.Name == "tasks");
        // tasks in simple-tasks declares audit:true
        tasks.Fields.Select(f => f.Name).ShouldContain("created_at");
        tasks.Fields.Select(f => f.Name).ShouldContain("updated_by");
    }

    [Fact]
    public async Task Complex_crm_maps_to_a_stable_model()
    {
        var m = Map("complex-crm/crm.alvo.json");
        await Verify(m); // snapshot: freezes mapping incl. tenant_id, generated cols, refs
    }
}
