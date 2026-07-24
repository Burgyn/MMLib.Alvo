using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Descriptor;

public class DescriptorToSchemaMapperTests
{
    private static SchemaModel Map(string file)
        => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(
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
    public void Soft_delete_column_is_nullable_and_audit_timestamps_are_required()
    {
        // simple-tasks' "projects" entity declares both audit:true and softDelete:true.
        var m = Map("simple-tasks/tasks.alvo.json");
        var projects = m.Entities.Single(e => e.Name == "projects");

        var createdAt = projects.Fields.Single(f => f.Name == "created_at");
        createdAt.Required.ShouldBeTrue();
        createdAt.Nullable.ShouldBeFalse();

        var createdBy = projects.Fields.Single(f => f.Name == "created_by");
        createdBy.Required.ShouldBeFalse();
        createdBy.Nullable.ShouldBeTrue();

        var deletedAt = projects.Fields.Single(f => f.Name == "deleted_at");
        deletedAt.Required.ShouldBeFalse();
        deletedAt.Nullable.ShouldBeTrue();
    }

    [Fact]
    public async Task Complex_crm_maps_to_a_stable_model()
    {
        var m = Map("complex-crm/crm.alvo.json");
        await Verify(m); // snapshot: freezes mapping incl. tenant_id, generated cols, refs
    }
}
