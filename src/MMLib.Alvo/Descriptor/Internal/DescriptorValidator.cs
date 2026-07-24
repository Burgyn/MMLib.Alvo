using Json.Pointer;
using Json.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// Layered descriptor validator: (1) a JsonSchema.Net pass against the embedded
/// project.schema.json, (2) a semantic pass for cross-field rules the schema cannot express, each
/// producing agent-first <see cref="DescriptorValidationError"/>s with fix suggestions.
/// </summary>
/// <remarks>
/// JsonSchema.Net (json-everything) evaluates schemas purely at runtime against
/// <see cref="System.Text.Json"/> — no Roslyn codegen, so no <c>PreserveCompilationContext</c>
/// requirement on the host, unlike the Corvus.Json.Validator this replaced.
/// </remarks>
internal sealed class DescriptorValidator : IDescriptorValidator
{
    private static readonly JsonSchema _schema = JsonSchema.FromText(DescriptorSchemaSource.Json);

    private static readonly EvaluationOptions _evaluationOptions = new() { OutputFormat = OutputFormat.List };

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
        var results = _schema.Evaluate(root, _evaluationOptions);
        if (results.IsValid)
        {
            return [];
        }

        return (results.Details ?? [])
            .Where(HasReportableError)
            .SelectMany(SchemaErrorsForNode);
    }

    /// <summary>
    /// A failing node inside an <c>if</c> condition subschema (the probe for <c>if</c>/<c>then</c>/<c>else</c>)
    /// is not itself a descriptor defect: it just means that branch's <c>then</c>/<c>else</c> did not apply.
    /// Only report nodes outside any <c>if</c> condition.
    /// </summary>
    private static bool HasReportableError(EvaluationResults node) =>
        node.Errors is { Count: > 0 } && !IsInsideIfCondition(node.EvaluationPath);

    private static bool IsInsideIfCondition(JsonPointer schemaPath)
    {
        for (var i = 0; i < schemaPath.SegmentCount; i++)
        {
            if (schemaPath.GetSegment(i).Equals("if"))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<DescriptorValidationError> SchemaErrorsForNode(EvaluationResults node)
    {
        var instancePath = PointerOrRoot(node.InstanceLocation.ToString());
        var schemaPath = PointerOrRoot(node.EvaluationPath.ToString());
        foreach (var (keyword, message) in node.Errors!)
        {
            var effectiveMessage = string.IsNullOrWhiteSpace(message)
                ? $"Value does not satisfy the '{keyword}' constraint."
                : message;
            yield return new DescriptorValidationError(
                instancePath,
                effectiveMessage,
                FixSuggestionFor(keyword, instancePath, schemaPath, effectiveMessage),
                DescriptorValidationSeverity.Error);
        }
    }

    private static string FixSuggestionFor(string keyword, string instancePath, string schemaPath, string message)
    {
        var keywordLabel = string.IsNullOrEmpty(keyword) ? "schema" : $"'{keyword}'";
        return $"Schema keyword {keywordLabel} failed for instance path '{instancePath}' " +
            $"(schema location '{schemaPath}'): {message} — see schema/project.schema.json there.";
    }

    private static string PointerOrRoot(string pointer) => pointer.Length == 0 ? "/" : pointer;

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
