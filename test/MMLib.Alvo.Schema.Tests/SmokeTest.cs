namespace MMLib.Alvo.Schema.Tests;

public class SmokeTest
{
    [Fact]
    public void Schema_file_exists() => File.Exists(SchemaPaths.SchemaFile).ShouldBeTrue();
}
