using Corvus.Json;
using Corvus.Json.Validator;
using System.Text.Json;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// Layered descriptor validator: (1) a Corvus JSON-schema pass against the embedded
/// project.schema.json, (2) a semantic pass for cross-field rules Corvus cannot express, each
/// producing agent-first <see cref="DescriptorValidationError"/>s with fix suggestions.
/// </summary>
/// <remarks>
/// Corvus.Json.Validator compiles the schema's generated types at runtime via Roslyn, resolving
/// reference assemblies from <c>DependencyContext.Default.CompileLibraries</c> — populated only
/// when the running executable sets MSBuild's <c>PreserveCompilationContext</c> to
/// <see langword="true"/>. Any host (standalone or embedded) that constructs this type must set
/// that property on its own executable project, or the static <see cref="_schema"/> field throws
/// on first use (Roslyn diagnostic CS0518, "Predefined type 'System.Object' is not defined").
/// </remarks>
internal sealed class DescriptorValidator : IDescriptorValidator
{
    private static readonly JsonSchema _schema = JsonSchema.FromText(DescriptorSchemaSource.Json);

    public DescriptorValidationResult Validate(string descriptorJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(descriptorJson);
        }
        catch (JsonException ex)
        {
            return new DescriptorValidationResult([Malformed(ex)]);
        }

        using (document)
        {
            var errors = new List<DescriptorValidationError>();
            errors.AddRange(SchemaErrors(document.RootElement));
            errors.AddRange(SemanticErrors(document.RootElement));
            return new DescriptorValidationResult(errors);
        }
    }

    private static DescriptorValidationError Malformed(JsonException ex) =>
        new("/", $"Descriptor is not valid JSON: {ex.Message}", "Fix the JSON syntax.", DescriptorValidationSeverity.Error);

    private static IEnumerable<DescriptorValidationError> SchemaErrors(JsonElement root)
    {
        var context = _schema.Validate(root, ValidationLevel.Detailed);
        if (context.IsValid)
        {
            return [];
        }

        return context.Results
            .Where(r => !r.Valid)
            .Select(r => new DescriptorValidationError(
                r.Location?.DocumentLocation.ToString() ?? "/",
                MessageOrFallback(r),
                FixSuggestion,
                DescriptorValidationSeverity.Error));
    }

    private static string MessageOrFallback(ValidationResult result) =>
        string.IsNullOrWhiteSpace(result.Message)
            ? "Value does not satisfy the project schema at this location."
            : result.Message;

    private const string FixSuggestion = "See schema/project.schema.json for the allowed shape at this path.";

    private static List<DescriptorValidationError> SemanticErrors(JsonElement root)
    {
        if (!root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var entityNames = entities.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var errors = new List<DescriptorValidationError>();
        foreach (var entity in entities.EnumerateObject())
        {
            errors.AddRange(EntitySemanticErrors(entity, entityNames));
        }

        return errors;
    }

    private static IEnumerable<DescriptorValidationError> EntitySemanticErrors(
        JsonProperty entity, HashSet<string> entityNames)
    {
        if (!entity.Value.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var field in fields.EnumerateObject())
        {
            var path = $"/entities/{entity.Name}/fields/{field.Name}";
            if (field.Value.TryGetProperty("computed", out _))
            {
                yield return new DescriptorValidationError(
                    path,
                    "Computed fields are not supported yet.",
                    "Remove 'computed' or track the CEL→SQL compiler in #21.",
                    DescriptorValidationSeverity.Error);
            }

            if (IsUnknownRef(field.Value, entityNames, out var target))
            {
                yield return new DescriptorValidationError(
                    path,
                    $"Field references unknown entity '{target}'.",
                    $"Add an entity named '{target}', or point 'entity' at an existing one.",
                    DescriptorValidationSeverity.Error);
            }
        }
    }

    /// <summary>
    /// Reserved entity name for the built-in auth entity (schema: <c>entities.users</c> is
    /// forbidden as a descriptor-declared key, but <c>ref</c> fields may target it — see
    /// schema/project.schema.json's "entities" and "field.entity" descriptions).
    /// </summary>
    private const string ReservedUsersEntity = "users";

    private static bool IsUnknownRef(JsonElement field, HashSet<string> entityNames, out string target)
    {
        target = "";
        if (!field.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        target = entity.GetString() ?? "";
        return target.Length > 0
            && target != ReservedUsersEntity
            && !entityNames.Contains(target);
    }
}
