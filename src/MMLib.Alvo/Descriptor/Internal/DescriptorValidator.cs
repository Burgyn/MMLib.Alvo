using Corvus.Json;
using MMLib.Alvo.Descriptor.SchemaGen;
using System.Text.Json;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// Layered descriptor validator: (1) a schema pass against the build-time Corvus-generated
/// <see cref="GeneratedProjectDescriptor"/> (from project.schema.json), (2) a semantic pass for
/// cross-field rules the schema cannot express, each producing agent-first
/// <see cref="DescriptorValidationError"/>s with fix suggestions.
/// </summary>
/// <remarks>
/// Corvus.Json.SourceGenerator emits <see cref="GeneratedProjectDescriptor"/> at compile time —
/// the schema is fixed at build time (Alvo's own per-version descriptor grammar), but the
/// runtime JSON being validated is fully arbitrary/untrusted (CLI/dashboard/API input). At
/// runtime this is a plain compiled .NET type: no Roslyn, no <c>PreserveCompilationContext</c>,
/// unlike the Corvus.Json.Validator (runtime-Roslyn) package Alvo used before this rework.
/// </remarks>
internal sealed class DescriptorValidator : IDescriptorValidator
{
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

    private static List<DescriptorValidationError> SchemaErrors(JsonElement root)
    {
        var instance = new GeneratedProjectDescriptor(root);
        var context = instance.Validate(ValidationContext.ValidContext, ValidationLevel.Detailed);
        if (context.IsValid)
        {
            return [];
        }

        return context.Results
            .Where(r => !r.Valid)
            .Select(ToError)
            .ToList();
    }

    private static DescriptorValidationError ToError(ValidationResult result)
    {
        var instancePath = PointerOrRoot(result.Location?.DocumentLocation.ToString());
        var schemaPath = PointerOrRoot(result.Location?.SchemaLocation.ToString());
        var message = result.Message;
        var effectiveMessage = string.IsNullOrWhiteSpace(message)
            ? $"Value does not satisfy the schema at '{schemaPath}'."
            : message;
        return new DescriptorValidationError(
            instancePath,
            effectiveMessage,
            FixSuggestionFor(instancePath, schemaPath, effectiveMessage),
            DescriptorValidationSeverity.Error);
    }

    private static string FixSuggestionFor(string instancePath, string schemaPath, string message)
    {
        var keyword = KeywordFrom(schemaPath);
        var keywordLabel = keyword is null ? "schema" : $"'{keyword}'";
        return $"Schema keyword {keywordLabel} failed for instance path '{instancePath}' " +
            $"(schema location '{schemaPath}'): {message} — see schema/project.schema.json there.";
    }

    /// <summary>The failing keyword is the last non-numeric segment of the schema pointer (numeric segments are array/oneOf indices).</summary>
    private static string? KeywordFrom(string schemaPath) =>
        schemaPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(segment => !int.TryParse(segment, out _));

    private static string PointerOrRoot(string? pointer) => string.IsNullOrEmpty(pointer) ? "/" : pointer;

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

    // TODO(#F7): dynamic entities (evidencie) will also be valid ref targets that never appear
    // as a declared key here — this exemption will need to generalize from a single reserved
    // name to "known at runtime, not from the descriptor" once that late binding lands.
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
