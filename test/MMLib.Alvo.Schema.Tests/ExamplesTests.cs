namespace MMLib.Alvo.Schema.Tests;

public class ExamplesTests
{
    public static IEnumerable<object[]> Positive() =>
        SchemaPaths.Examples().Select(path => new object[] { path });

    public static IEnumerable<object[]> Negative() =>
        SchemaPaths.NegativeExamples().Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(Positive))]
    public void Example_validates(string path)
    {
        var schema = SchemaValidator.Load();
        var failures = SchemaValidator.Failures(schema, File.ReadAllText(path));
        failures.ShouldBeEmpty($"{Path.GetFileName(path)} must validate against the schema");
    }

    [Theory]
    [MemberData(nameof(Negative))]
    public void Negative_example_is_rejected(string path)
    {
        var schema = SchemaValidator.Load();
        var failures = SchemaValidator.Failures(schema, File.ReadAllText(path));
        failures.ShouldNotBeEmpty($"{Path.GetFileName(path)} must be rejected by the schema");
    }
}
