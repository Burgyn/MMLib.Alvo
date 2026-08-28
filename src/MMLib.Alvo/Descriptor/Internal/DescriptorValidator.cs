using Corvus.Json;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Descriptor.SchemaGen;
using MMLib.Alvo.Events.Internal;
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
            errors.AddRange(WildcardSubscriptionErrors(document.RootElement));
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

    /// <summary>
    /// The structured half of the wildcard-subscription refusal <c>DescriptorToSchemaMapper</c> throws for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two-pass tie <see cref="UnhonouredFeatures"/>' remarks describe, for a slot that is top-level
    /// rather than per entity.</b> The typed pass in the mapper is what an embedded host calling
    /// <c>FromDescriptor</c> passes through; this raw-JSON pass is what gives a CLI, a dashboard or an agent
    /// the JSON Pointer and the fix suggestion an exception message cannot carry. Both read
    /// <see cref="UnhonouredFeatures.WildcardSubscription"/> for the words, so the two cannot describe the
    /// same refusal differently.
    /// </para>
    /// <para>
    /// It runs beside <see cref="SemanticErrors"/> rather than inside it because that walk is keyed on
    /// <c>entities</c> and returns early for a descriptor without one — and a descriptor may declare
    /// automation over an entity set this build is not mapping.
    /// </para>
    /// </remarks>
    /// <param name="root">The descriptor's root object.</param>
    private static List<DescriptorValidationError> WildcardSubscriptionErrors(JsonElement root)
    {
        var errors = new List<DescriptorValidationError>();
        foreach (var block in _eventPatternBlocks)
        {
            if (!root.TryGetProperty(block, out var declared) || declared.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            errors.AddRange(declared.EnumerateObject()
                .Select(entry => WildcardErrorFor(block, entry))
                .OfType<DescriptorValidationError>());
        }

        return errors;
    }

    /// <summary>The error one automation rule or function earns, or <see langword="null"/> when it is exact.</summary>
    /// <remarks>
    /// <b>Every step checks <see cref="JsonElement.ValueKind"/> before reading, including the entry itself.</b>
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> <em>throws</em> on a non-object rather
    /// than answering <see langword="false"/>, and this walk runs on raw input before the schema pass has
    /// gated anything — so <c>"automation": { "deal-won": "not-an-object" }</c> is syntactically valid JSON
    /// that would take the whole validator down. <see cref="IDescriptorValidator"/>'s contract is to
    /// <em>report</em> on arbitrary input and never throw, and a crash on the apply path is an availability
    /// bug on caller-controlled input. Matches <c>Declares</c>' convention in this same file.
    /// </remarks>
    /// <param name="block">The top-level block the entry sits in.</param>
    /// <param name="entry">One rule or function, by its declared name.</param>
    private static DescriptorValidationError? WildcardErrorFor(string block, JsonProperty entry)
    {
        if (entry.Value.ValueKind != JsonValueKind.Object
            || !entry.Value.TryGetProperty("trigger", out var trigger)
            || trigger.ValueKind != JsonValueKind.Object
            || !trigger.TryGetProperty("event", out var pattern)
            || pattern.ValueKind != JsonValueKind.String
            || !EventPattern.HasWildcard(pattern.GetString()!))
        {
            return null;
        }

        var refusal = UnhonouredFeatures.WildcardSubscription;
        return new DescriptorValidationError(
            $"/{block}/{PointerToken(entry.Name)}/trigger/event",
            $"'{pattern.GetString()}' subscribes with a wildcard. {refusal.Consequence}",
            refusal.Fix,
            DescriptorValidationSeverity.Error);
    }

    /// <summary>One JSON Pointer reference token, escaped per RFC 6901 §3.</summary>
    /// <param name="name">The rule or function name, exactly as the descriptor spells it.</param>
    /// <remarks>
    /// <c>~</c> becomes <c>~0</c> and <c>/</c> becomes <c>~1</c>, <b>in that order</b> — reversing them would
    /// re-escape the tilde this method just introduced. Without it, a rule named <c>a/b</c> produced a pointer
    /// addressing a different location than the one that was refused, so an agent or dashboard following the
    /// path would land somewhere else entirely. The names are author-supplied and the schema's own
    /// <c>propertyNames</c> does not forbid either character.
    /// </remarks>
    private static string PointerToken(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// The top-level blocks whose entries carry a <c>$defs/eventPattern</c>-typed trigger, in the order
    /// <c>schema/project.schema.json</c> declares them.
    /// </summary>
    private static readonly string[] _eventPatternBlocks = ["automation", "functions"];

    private static List<DescriptorValidationError> SemanticErrors(JsonElement root)
    {
        if (!root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var entityNames = entities.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var tenancyEnabled = IsTenancyEnabled(root);
        var errors = new List<DescriptorValidationError>();
        foreach (var entity in entities.EnumerateObject())
        {
            errors.AddRange(EntitySemanticErrors(entity, entityNames, tenancyEnabled));
        }

        return errors;
    }

    /// <summary>
    /// Whether the project turns tenancy on, which is what makes an entity that says nothing about tenancy
    /// scoped — and therefore carry a framework-managed <c>tenant_id</c>.
    /// </summary>
    /// <remarks>
    /// Read from the root here and threaded down, rather than left out, because <c>tenant_id</c>'s membership in
    /// the managed set is a <em>project</em>-level answer for an entity that declares no <c>tenancy</c> of its
    /// own (<c>DescriptorToSchemaMapper.ResolveTenancy</c>). Omitting it would make this pass under-report
    /// exactly the entities a multi-tenant project is built from — the mapper would still refuse them, but with
    /// an exception instead of a path and a fix, which is the shape a dashboard cannot show.
    /// </remarks>
    /// <param name="root">The descriptor's root object.</param>
    private static bool IsTenancyEnabled(JsonElement root) =>
        root.TryGetProperty("tenancy", out var tenancy)
        && tenancy.ValueKind == JsonValueKind.Object
        && tenancy.TryGetProperty("enabled", out var enabled)
        && enabled.ValueKind == JsonValueKind.True;

    private static IEnumerable<DescriptorValidationError> EntitySemanticErrors(
        JsonProperty entity, HashSet<string> entityNames, bool tenancyEnabled)
    {
        foreach (var error in Unhonoured($"/entities/{entity.Name}", entity.Value, UnhonouredFeatures.OnAnEntity))
        {
            yield return error;
        }

        if (!entity.Value.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var managed = ManagedColumnsOf(entity.Value, tenancyEnabled);
        foreach (var field in fields.EnumerateObject())
        {
            foreach (var error in FieldSemanticErrors(
                $"/entities/{entity.Name}/fields/{field.Name}", field, entityNames, managed))
            {
                yield return error;
            }
        }
    }

    /// <summary>
    /// The framework-managed columns this entity carries, read from its traits in the raw JSON.
    /// </summary>
    /// <remarks>
    /// The trait rule itself is <see cref="AlvoManagedColumns"/>', reached through
    /// <see cref="ManagedColumnNames.InjectedFor"/> — this only maps the three JSON flags onto it. It has to be
    /// answered from traits rather than from a flat name list because an entity without <c>audit</c> may
    /// legitimately declare an ordinary field called <c>created_at</c>, and refusing that would refuse a field
    /// the framework does not manage.
    /// </remarks>
    /// <param name="entity">The entity object.</param>
    /// <param name="tenancyEnabled">Whether the project turns tenancy on.</param>
    private static IReadOnlySet<string> ManagedColumnsOf(JsonElement entity, bool tenancyEnabled) =>
        ManagedColumnNames.InjectedFor(
            TenancyOf(entity, tenancyEnabled),
            audit: IsTrue(entity, "audit"),
            softDelete: IsTrue(entity, "softDelete"));

    /// <summary>
    /// The entity's resolved tenancy, parsed from raw JSON and then <b>defaulted by the mapper's own rule</b>
    /// rather than by a copy of it.
    /// </summary>
    /// <remarks>
    /// This pass and <c>DescriptorToSchemaMapper</c> must agree exactly: this one decides whether declaring
    /// <c>tenant_id</c> is refused, that one decides whether <c>tenant_id</c> is injected. A copied defaulting
    /// rule — which is what this was — makes a divergence produce a descriptor the validator accepts and the
    /// mapper then refuses with an exception, i.e. a structured error a dashboard never gets to show. Only the
    /// <em>parsing</em> is local, because raw JSON is why this pass exists.
    /// </remarks>
    /// <param name="entity">The entity object.</param>
    /// <param name="tenancyEnabled">Whether the project turns tenancy on.</param>
    private static TenancyMode? TenancyOf(JsonElement entity, bool tenancyEnabled) =>
        DescriptorToSchemaMapper.ResolveTenancy(DeclaredTenancyOf(entity), tenancyEnabled);

    /// <summary>
    /// The tenancy the entity declares for itself, or <see langword="null"/> when it declares none — the one
    /// part of the question that is genuinely per-representation.
    /// </summary>
    /// <remarks>
    /// An unrecognised string reads as "declares none" rather than throwing: the schema pass already refuses a
    /// value outside the enum, and this pass runs even when that one has failed, so it must not turn a bad
    /// value into a second, worse diagnosis.
    /// </remarks>
    /// <param name="entity">The entity object.</param>
    private static TenancyMode? DeclaredTenancyOf(JsonElement entity) =>
        entity.TryGetProperty("tenancy", out var declared) && declared.ValueKind == JsonValueKind.String
            ? declared.GetString() switch
            {
                "scoped" => TenancyMode.Scoped,
                "global" => TenancyMode.Global,
                _ => null,
            }
            : null;

    private static bool IsTrue(JsonElement entity, string property) =>
        entity.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static IEnumerable<DescriptorValidationError> FieldSemanticErrors(
        string path, JsonProperty field, HashSet<string> entityNames, IReadOnlySet<string> managed)
    {
        foreach (var error in Unhonoured(path, field.Value, UnhonouredFeatures.OnAField))
        {
            yield return error;
        }

        if (IsUnknownRef(field.Value, entityNames, out var target))
        {
            yield return new DescriptorValidationError(
                path,
                $"Field references unknown entity '{target}'.",
                $"Add an entity named '{target}', or point 'entity' at an existing one.",
                DescriptorValidationSeverity.Error);
        }

        if (managed.Contains(field.Name))
        {
            yield return DeclaresAManagedColumn(path, field.Name);
        }

        if (ReservedQueryKeys.IsReserved(field.Name))
        {
            yield return ShadowsAReservedQueryParameter(path, field.Name);
        }
    }

    /// <summary>
    /// Reports every feature <see cref="UnhonouredFeatures"/> records as declared-and-unhonoured that this
    /// descriptor node declares, as a structured error carrying the JSON path, the consequence and the fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The table is shared with the mapper, and that is the whole point of it.</b> The mapper throws for
    /// each of these; this pass reports all of them at once with a path and a fix suggestion (§0 principle 4),
    /// which is the only form a dashboard or a CLI <c>validate</c> can show. They were two hand-written
    /// lists plus two more in the test files, and <c>validation</c> was silently dropped for a whole task
    /// because a fifth <c>if</c> is an easy thing to forget.
    /// </para>
    /// <para>
    /// A feature is detected here from raw JSON, before anything is parsed, because this pass runs even when
    /// the schema pass has already failed — so it cannot depend on the descriptor being parseable. The
    /// table's <see cref="UnhonouredFeature{T}.Path"/> is a JSON Pointer path precisely so a nested
    /// declaration (a single <c>hooks/beforeUpdate</c> point) is found by walking it, and the pointer this
    /// error reports is built from the same string that found it.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The descriptor type the table's predicate inspects; unused here, since this pass reads JSON.</typeparam>
    /// <param name="path">The JSON pointer of the field or entity.</param>
    /// <param name="node">Its raw JSON.</param>
    /// <param name="unhonoured">The table to report from.</param>
    private static IEnumerable<DescriptorValidationError> Unhonoured<T>(
        string path, JsonElement node, IReadOnlyList<UnhonouredFeature<T>> unhonoured)
    {
        foreach (var feature in unhonoured.Where(feature => Declares(node, feature.Path)))
        {
            yield return new DescriptorValidationError(
                $"{path}/{feature.Path}", feature.Consequence, feature.Fix, DescriptorValidationSeverity.Error);
        }
    }

    /// <summary>
    /// Whether <paramref name="node"/> declares the feature at <paramref name="featurePath"/>, walking a
    /// slash-separated path so a nested hook point is found as precisely as a top-level key.
    /// </summary>
    /// <remarks>
    /// A declaration is a present, <em>non-empty</em> value. <c>softDelete: false</c> is not a declaration —
    /// PR2 established that, and the negative leg is asserted — and neither is <c>"beforeUpdate": []</c>, an
    /// empty list that asks for nothing. Both would otherwise be refused for declaring a feature they
    /// decline to use, which is a refusal an author cannot act on.
    /// </remarks>
    /// <param name="node">The field's or entity's raw JSON.</param>
    /// <param name="featurePath">The table's slash-separated path.</param>
    private static bool Declares(JsonElement node, string featurePath)
    {
        var current = node;
        foreach (var segment in featurePath.Split('/'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.Array => current.GetArrayLength() > 0,
            _ => true,
        };
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

    /// <summary>
    /// The refusal for a field that names a column the framework injects for this entity's traits.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="ShadowsAReservedQueryParameter"/> and deliberately shaped like it — both say
    /// "this name is not yours to use", and both are answered here rather than at request time. The prose comes
    /// from <see cref="ManagedColumnNames"/>, which the mapper's own refusal reads too, so a declaration cannot
    /// be explained one way by the validator and another by the apply that follows it.
    /// </remarks>
    /// <param name="path">The field's JSON pointer.</param>
    /// <param name="field">The managed column the entity declares.</param>
    private static DescriptorValidationError DeclaresAManagedColumn(string path, string field)
    {
        var (consequence, fix) = ManagedColumnNames.Refusing(field);
        return new(
            path,
            $"Field '{field}' is a framework-managed column and cannot be declared. {consequence}",
            fix,
            DescriptorValidationSeverity.Error);
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
