using MMLib.Alvo.DocsGen.Schema;

namespace MMLib.Alvo.DocsGen.Tests.Schema;

public class SchemaFileGeneratorTests
{
    [Fact]
    public async Task The_served_schema_is_the_repository_schema()
    {
        var root = RepositoryRoot.Find();
        var context = new DocsGenContext(new DocsGenPaths(root, Path.Combine(root, "website"), Path.Combine(root, "website", "reference")));

        var pages = await new SchemaFileGenerator().GenerateAsync(context, TestContext.Current.CancellationToken);

        pages.ShouldHaveSingleItem().ShouldBe(new GeneratedPage(OutputRoot.Public, "schema/v1/project.json", await File.ReadAllTextAsync(RealSchema.Path, TestContext.Current.CancellationToken)));
    }
}
