namespace MMLib.Alvo.Schema.Tests;

public class SnapshotTests
{
    [Fact]
    public Task Canonical_complex_crm() =>
        Verify(Canonicalizer.Canonicalize(
            File.ReadAllText(SchemaPaths.Examples().First(path => path.Contains("complex-crm")))))
            .UseFileName("canonical-complex-crm");

    [Fact]
    public Task Negative_error_output()
    {
        var schema = SchemaValidator.Load();
        var report = SchemaPaths.NegativeExamples().Select(path => new
        {
            file = Path.GetFileName(path),
            failures = SchemaValidator.Failures(schema, File.ReadAllText(path))
                .Select(failure => new { failure.Pointer, failure.Message })
                .OrderBy(failure => failure.Pointer, StringComparer.Ordinal)
                .ThenBy(failure => failure.Message, StringComparer.Ordinal)
                .ToList(),
        });

        return Verify(report).UseFileName("negative-error-output");
    }
}
