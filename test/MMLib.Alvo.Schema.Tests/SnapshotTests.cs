namespace MMLib.Alvo.Schema.Tests;

public class SnapshotTests
{
    [Fact]
    public Task Canonical_complex_crm() =>
        Verify(Canonicalizer.Canonicalize(
            File.ReadAllText(SchemaPaths.Examples().First(path => path.Contains("complex-crm")))))
            .UseFileName("canonical-complex-crm");

    // Corvus emits no message for some keyword failures (const, additionalProperties, a
    // boolean-false subschema like the reserved "users" entity). Make that explicit in the golden
    // file instead of freezing a bare blank that reads like a bug — the pointer still pins where the
    // failure fired. Rich, fix-suggesting messages are the deferred runtime validator's job (#20).
    private const string NoMessagePlaceholder = "(schema keyword failure — validator emits no message)";

    [Fact]
    public Task Negative_error_output()
    {
        var schema = SchemaValidator.Load();
        var report = SchemaPaths.NegativeExamples().Select(path => new
        {
            file = Path.GetFileName(path),
            failures = SchemaValidator.Failures(schema, File.ReadAllText(path))
                .Select(failure => new
                {
                    failure.Pointer,
                    Message = string.IsNullOrWhiteSpace(failure.Message) ? NoMessagePlaceholder : failure.Message,
                })
                .OrderBy(failure => failure.Pointer, StringComparer.Ordinal)
                .ThenBy(failure => failure.Message, StringComparer.Ordinal)
                .ToList(),
        });

        return Verify(report).UseFileName("negative-error-output");
    }
}
