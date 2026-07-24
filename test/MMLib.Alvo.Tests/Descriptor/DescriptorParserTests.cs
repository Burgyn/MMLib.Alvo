using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Testing;
using Xunit;

namespace MMLib.Alvo.Tests.Descriptor;

public class DescriptorParserTests
{
    private static string SimpleTasks() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", "simple-tasks", "tasks.alvo.json"));

    [Fact]
    public void Parses_name_and_entities()
    {
        AlvoDescriptor d = AlvoDescriptor.Parse(SimpleTasks());
        Assert.Equal("simple-tasks", d.Name);
        Assert.Contains("tasks", d.Entities.Keys);
        Assert.NotEmpty(d.Entities["tasks"].Fields);
    }
}
