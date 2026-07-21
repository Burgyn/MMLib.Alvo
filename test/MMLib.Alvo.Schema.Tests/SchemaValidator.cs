using Corvus.Json;
using Corvus.Json.Validator;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

internal static class SchemaValidator
{
    internal static JsonSchema Load() => JsonSchema.FromFile(SchemaPaths.SchemaFile);

    internal static IReadOnlyList<(string Pointer, string Message)> Failures(JsonSchema schema, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ValidationContext context = schema.Validate(document.RootElement, ValidationLevel.Detailed);
        if (context.IsValid)
        {
            return [];
        }

        return context.Results
            .Where(result => !result.Valid)
            .Select(result => (
                result.Location?.DocumentLocation.ToString() ?? string.Empty,
                result.Message ?? string.Empty))
            .ToList();
    }
}
