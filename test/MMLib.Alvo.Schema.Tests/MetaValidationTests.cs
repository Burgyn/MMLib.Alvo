using Corvus.Json;
using Corvus.Json.Validator;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

public class MetaValidationTests
{
    [Fact]
    public void Schema_is_a_valid_draft_2020_12_document()
    {
        // In draft 2020-12 `format` is annotation-only (format-annotation vocabulary);
        // a conformant meta-validation must not assert it, so our `pattern`/regex values
        // aren't spuriously rejected by the meta-schema's `format: regex` on `pattern`.
        JsonSchema metaSchema = JsonSchema.FromFile(
            SchemaPaths.MetaSchemaFile,
            new JsonSchema.Options(alwaysAssertFormat: false));
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(SchemaPaths.SchemaFile));

        ValidationContext result = metaSchema.Validate(schema.RootElement, ValidationLevel.Detailed);

        result.IsValid.ShouldBeTrue(
            "schema/project.schema.json must be a valid draft 2020-12 schema. First failures: "
            + string.Join(
                " | ",
                result.Results
                    .Where(r => !r.Valid)
                    .Take(5)
                    .Select(r => $"{r.Location?.DocumentLocation}: {r.Message}")));
    }

    [Fact]
    public void Every_declared_property_has_a_description()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SchemaPaths.SchemaFile));
        List<string> missing = [];
        WalkProperties(document.RootElement, "#", missing);
        missing.ShouldBeEmpty("every declared property must carry a description (agent + IntelliSense UX)");
    }

    // Only real property declarations need a description; skip constraint contexts
    // (if/then/else/not), a bare $ref (inherits the target's description), and
    // discriminator const / boolean schemas (e.g. reserved `users: false`).
    private static readonly string[] _constraintKeywords = ["if", "then", "else", "not"];

    private static void WalkProperties(JsonElement node, string pointer, List<string> missing)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in node.EnumerateArray())
                {
                    WalkProperties(item, $"{pointer}/{index++}", missing);
                }

                return;
            case JsonValueKind.Object:
                break;
            default:
                return;
        }

        foreach (JsonProperty member in node.EnumerateObject())
        {
            if (_constraintKeywords.Contains(member.Name))
            {
                continue;
            }

            if (member.Name == "properties" && member.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in member.Value.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    bool exempt = property.Value.TryGetProperty("description", out _)
                        || property.Value.TryGetProperty("$ref", out _)
                        || property.Value.TryGetProperty("const", out _);
                    if (!exempt)
                    {
                        missing.Add($"{pointer}/properties/{property.Name}");
                    }
                }
            }

            WalkProperties(member.Value, $"{pointer}/{member.Name}", missing);
        }
    }
}
