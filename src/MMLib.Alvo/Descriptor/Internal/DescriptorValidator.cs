using Corvus.Json;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Descriptor.SchemaGen;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// Layered descriptor validator: (1) a schema pass against the build-time Corvus-generated
/// <see cref="GeneratedProjectDescriptor"/> (from project.schema.json), (2) a semantic pass for
/// cross-field rules the schema cannot express, (3) a rule-compilation pass that runs every
/// <c>rules</c>/<c>hidden</c>/<c>readOnly</c> CEL expression through <see cref="ICelCompiler"/> —
/// each producing agent-first <see cref="DescriptorValidationError"/>s with fix suggestions, so a
/// rule that references an unknown column or the retired singular <c>@user.role</c> idiom fails
/// here, when the descriptor is applied, never at request time.
/// </summary>
/// <remarks>
/// <para>
/// Corvus.Json.SourceGenerator emits <see cref="GeneratedProjectDescriptor"/> at compile time —
/// the schema is fixed at build time (Alvo's own per-version descriptor grammar), but the
/// runtime JSON being validated is fully arbitrary/untrusted (CLI/dashboard/API input). At
/// runtime this is a plain compiled .NET type: no Roslyn, no <c>PreserveCompilationContext</c>,
/// unlike the Corvus.Json.Validator (runtime-Roslyn) package Alvo used before this rework.
/// </para>
/// <para>
/// The rule-compilation pass runs only when the schema pass produced no errors — a descriptor the
/// schema already rejects cannot reliably be parsed into <see cref="AlvoDescriptor"/> and mapped by
/// <see cref="DescriptorToSchemaMapper"/>. It compiles the same <see cref="PolicyCatalog"/> the
/// apply-time priming path (<c>PolicyCatalogPriming</c>, <c>SchemaMigrationRunner</c>,
/// <c>RuntimeSchemaService</c>) later builds for real; this pass discards the built catalog and
/// keeps only its errors, since <see cref="IDescriptorValidator.Validate"/>'s public contract
/// returns findings, not a catalog. A descriptor that passes this pass is compiled a second time by
/// the priming path immediately afterward — recompiling the same CEL strings once more is the
/// deliberate, documented cost of keeping <see cref="IDescriptorValidator"/>'s contract narrow
/// (report findings only) rather than smuggling an internal type through a public interface for a
/// rarely-hot path (schema/rule compilation runs once per apply, never per request).
/// </para>
/// </remarks>
internal sealed class DescriptorValidator : IDescriptorValidator
{
    private readonly ICelCompiler _compiler;

    /// <summary>
    /// Initializes a new instance of the <see cref="DescriptorValidator"/> class with the default CEL
    /// compiler — for a caller that has no container to resolve one from (a test, a CLI validate
    /// command). A host that replaced <see cref="ICelCompiler"/> must not use this overload: it would
    /// validate rules against a different compiler from the one the apply path then compiles them
    /// with, so the pass that reports findings and the pass that builds the real catalog could
    /// disagree. The DI registration always uses the other constructor.
    /// </summary>
    public DescriptorValidator()
        : this(new CelCompiler())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DescriptorValidator"/> class.</summary>
    /// <param name="compiler">The CEL compiler every rule and field flag is compiled through.</param>
    public DescriptorValidator(ICelCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        _compiler = compiler;
    }

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
            var schemaErrors = SchemaErrors(document.RootElement);
            var errors = new List<DescriptorValidationError>(schemaErrors);
            errors.AddRange(SemanticErrors(document.RootElement));
            if (schemaErrors.Count == 0)
            {
                errors.AddRange(RuleErrors(descriptorJson));
            }

            return new DescriptorValidationResult(errors);
        }
    }

    private List<DescriptorValidationError> RuleErrors(string descriptorJson)
    {
        AlvoDescriptor descriptor;
        SchemaModel schema;
        try
        {
            descriptor = AlvoDescriptor.Parse(descriptorJson);
            schema = DescriptorToSchemaMapper.Map(descriptor);
        }
        catch (InvalidDataException)
        {
            // Already reported by the semantic pass above (today's 'computed' rejection) — do not
            // double-report the same field, and a mapping failure leaves nothing to compile rules against.
            return [];
        }

        return PolicyCatalog.TryBuild(descriptor, schema, _compiler, out _, out var errors)
            ? []
            : [.. errors];
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
        if (DeclaresSoftDelete(entity.Value))
        {
            yield return new DescriptorValidationError(
                $"/entities/{entity.Name}/softDelete",
                "Soft delete is not supported yet: a delete would remove the row and reads would not exclude it.",
                "Remove 'softDelete' or track the soft-delete implementation issue.",
                DescriptorValidationSeverity.Error);
        }

        if (!entity.Value.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var field in fields.EnumerateObject())
        {
            foreach (var error in FieldSemanticErrors($"/entities/{entity.Name}/fields/{field.Name}", field, entityNames))
            {
                yield return error;
            }
        }
    }

    private static IEnumerable<DescriptorValidationError> FieldSemanticErrors(
        string path, JsonProperty field, HashSet<string> entityNames)
    {
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

        if (ReservedQueryKeys.IsReserved(field.Name))
        {
            yield return ShadowsAReservedQueryParameter(path, field.Name);
        }
    }

    /// <summary>
    /// A field whose name the Data API's query string reserves, refused at <b>apply</b> time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The descriptor's own field grammar (<c>^[a-z][a-z0-9_]{0,62}$</c>) accepts every reserved name, so
    /// <c>?limit=10</c> against an entity declaring a <c>limit</c> field is genuinely ambiguous. Resolving that
    /// per request — silently preferring one reading, or refusing the request — would make a descriptor problem
    /// look like a caller problem, against this framework's rule that a bad descriptor fails at save.
    /// </para>
    /// <para>
    /// <b>Here rather than only at route mapping</b> because the descriptor is wrong whether or not the API is
    /// mounted: an embedded host that never calls <c>MapAlvoDataApi</c> would otherwise get no refusal at all,
    /// and would discover the collision when it first exposed the entity. The mapping-time guard stays as the
    /// belt for a descriptor that was applied before this check existed.
    /// </para>
    /// <para>
    /// The reserved list belongs to the Data API and is read from there rather than restated, which is why this
    /// validator reaches across a feature boundary for it. One list is the point — a second copy here is how the
    /// apply-time refusal and the parser come to disagree about which names are reserved.
    /// </para>
    /// </remarks>
    private static DescriptorValidationError ShadowsAReservedQueryParameter(string path, string field) => new(
        path,
        $"Field name '{field}' is reserved by the Data API's query string, so a request could not tell a filter "
        + $"on this field from the '{field}' parameter itself.",
        $"Rename the field. The reserved names are {ReservedQueryKeys.AsList}.",
        DescriptorValidationSeverity.Error);

    private static bool DeclaresSoftDelete(JsonElement entity) =>
        entity.TryGetProperty("softDelete", out var softDelete)
        && softDelete.ValueKind == JsonValueKind.True;

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
