using MMLib.Alvo.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Reads an entity out of the working copy as a <see cref="EntitySchema"/>, so a pending entity
/// renders on the same screens as an applied one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> An entity added to the working copy does not exist in
/// <c>GET schema</c> — the schema is what the Data API serves, and nothing has been applied yet.
/// Without this, adding an entity would produce a screen saying <i>there is no entity called
/// invoices</i>, which is true of the database and useless to the person who just added it.
/// </para>
/// <para>
/// <b>It is a view, not a second parser.</b> It answers what the editor's own screens need — the
/// declared fields with their facets, and the entity's flags — and nothing else. The authority on
/// whether the descriptor is acceptable is still the apply: this never refuses anything, because a
/// pending entity that this could not read is one the preview is about to explain properly.
/// </para>
/// <para>
/// <b>What it deliberately does not do is round-trip.</b> The working copy stays JSON end to end
/// (§6.3-3); this is read-only, one way, and no edit ever travels back through it.
/// </para>
/// </remarks>
internal static class PendingSchema
{
    /// <summary>Reads one entity out of a working descriptor, when it declares one.</summary>
    /// <param name="descriptorJson">The working document.</param>
    /// <param name="entity">The entity to read.</param>
    /// <returns>The entity as the screens render it, or <see langword="null"/>.</returns>
    public static EntitySchema? Read(string descriptorJson, string entity)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(descriptorJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("entities", out var entities) || !entities.TryGetProperty(entity, out var declared))
            {
                return null;
            }

            var enabled = root.TryGetProperty("tenancy", out var tenancy) && tenancy.ValueKind == JsonValueKind.Object
                && Flag(tenancy, "enabled");
            return Entity(entity, declared, enabled);
        }
    }

    private static EntitySchema Entity(string entity, JsonElement declared, bool tenancyEnabled) => new()
    {
        Name = entity,
        Description = String(declared, "description"),
        Storage = StorageOf(declared),
        Tenancy = Tenancy(String(declared, "tenancy"), tenancyEnabled),
        Audit = Flag(declared, "audit"),
        SoftDelete = Flag(declared, "softDelete"),
        Fields = Fields(declared),
    };

    /// <summary>
    /// The entities a working document declares with <c>storage: dynamic</c> — which this build never creates, so the
    /// Schema list must not call them "not applied yet" forever (docs/todo-admin.md §8d item 21).
    /// </summary>
    /// <param name="descriptorJson">The working document.</param>
    public static IReadOnlySet<string> Dynamic(string descriptorJson)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(descriptorJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("entities", out var entities)
                && entities.ValueKind == JsonValueKind.Object)
            {
                names.UnionWith(entities.EnumerateObject()
                    .Where(entity => StorageOf(entity.Value) == EntityStorage.Dynamic)
                    .Select(entity => entity.Name));
            }
        }
        catch (JsonException)
        {
            /* A document that does not parse declares nothing; the preview explains it. */
        }

        return names;
    }

    /// <summary>
    /// The <c>storage: dynamic</c> entities <paramref name="descriptorJson"/> adds, or declares differently, against
    /// <paramref name="appliedJson"/> — what an apply records in the descriptor and never creates, which Preview names
    /// rather than calling the schema unchanged (B2 review, finding 3).
    /// </summary>
    /// <param name="appliedJson">The applied descriptor.</param>
    /// <param name="descriptorJson">The working document.</param>
    public static IReadOnlyList<string> DynamicChanged(string appliedJson, string descriptorJson)
    {
        var dynamic = Dynamic(descriptorJson);
        var applied = EntitiesOf(appliedJson);
        return [.. EntitiesOf(descriptorJson)
            .Where(entity => dynamic.Contains(entity.Key))
            .Where(entity => applied[entity.Key] is not { } before || !JsonNode.DeepEquals(before, entity.Value))
            .Select(entity => entity.Key)];
    }

    /// <summary>A document's entities, in declaration order, or none when it does not parse.</summary>
    private static JsonObject EntitiesOf(string descriptorJson)
    {
        try
        {
            return JsonNode.Parse(descriptorJson)?["entities"] as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The declared storage; the schema's default, physical, when it says none.</summary>
    private static EntityStorage StorageOf(JsonElement entity)
        => entity.ValueKind == JsonValueKind.Object && String(entity, "storage") == "dynamic"
            ? EntityStorage.Dynamic
            : EntityStorage.Physical;

    /// <summary>
    /// The mapper's own rule (<c>DescriptorToSchemaMapper.ResolveTenancy</c>): the declared tenancy, else scoped when
    /// the project turns tenancy on, else none — a pending entity with no key used to be drawn global in a project
    /// that would apply it scoped (§8a <c>tenancy.enabled</c> row).
    /// </summary>
    private static TenancyMode? Tenancy(string? declared, bool enabled) => declared switch
    {
        "scoped" => TenancyMode.Scoped,
        "global" => TenancyMode.Global,
        _ => enabled ? TenancyMode.Scoped : null,
    };

    private static IReadOnlyList<FieldSchema> Fields(JsonElement entity)
    {
        if (!entity.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. fields.EnumerateObject().Select(field => new FieldSchema
        {
            Name = field.Name,
            Type = Type(String(field.Value, "type")),
            Description = String(field.Value, "description"),
            Required = Flag(field.Value, "required"),
            Unique = Flag(field.Value, "unique"),
            MaxLength = Number(field.Value, "maxLength"),
            Precision = Number(field.Value, "precision"),
            Scale = Number(field.Value, "scale"),
            EnumValues = Values(field.Value),
            Reference = Reference(field.Value),
            Format = String(field.Value, "format"),
            ComputedExpression = String(field.Value, "computed"),
            Rollup = Rollup(field.Value),
            Indexed = Flag(field.Value, "index"),
            Nullable = NullableOf(field.Value),
            Default = Literal(field.Value),
        })];
    }

    /// <summary>The declared nullability, or the one <c>required</c> implies — the mapper's <c>f.Nullable ?? f.Required != true</c>.</summary>
    private static bool NullableOf(JsonElement field)
        => field.TryGetProperty("nullable", out var declared) && declared.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? declared.ValueKind == JsonValueKind.True
            : !Flag(field, "required");

    /// <summary>
    /// The declared literal default, cloned to outlive the document. A <c>$cel</c> object is not one — the build
    /// refuses it and the mapper resolves it to nothing (<c>FieldDefault.Resolve</c>) — so it is left out here too.
    /// </summary>
    private static JsonElement? Literal(JsonElement field)
        => field.TryGetProperty("default", out var declared) && declared.ValueKind != JsonValueKind.Null
            && !(declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty("$cel", out _))
            ? declared.Clone()
            : null;

    /// <summary>
    /// A staged rollup, so its row keeps the <c>rollup</c> badge the moment it is changed (§8d item 22).
    /// </summary>
    /// <remarks>
    /// <c>Via</c> is required on the applied shape because the resolver always resolves it; a staged one has not been
    /// resolved yet, so it is the declared <c>via</c> or empty — this is a renderer, and the apply resolves it.
    /// </remarks>
    private static RollupSchema? Rollup(JsonElement field)
        => field.TryGetProperty("rollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object
            && String(rollup, "from") is { Length: > 0 } from
            ? new RollupSchema
            {
                From = from,
                Op = Enum.TryParse<RollupOperation>(String(rollup, "op"), ignoreCase: true, out var op) ? op : RollupOperation.Count,
                Field = String(rollup, "field"),
                Via = String(rollup, "via") ?? string.Empty,
            }
            : null;

    /// <summary>
    /// The declared type, defaulting to <see cref="FieldType.String"/> for anything unreadable.
    /// </summary>
    /// <remarks>
    /// A default rather than a refusal, because this is a renderer: a field whose type the schema
    /// does not admit is a descriptor the apply rejects, and the preview says so precisely. Failing
    /// here would replace that explanation with a blank screen.
    /// </remarks>
    private static FieldType Type(string? declared)
        => Enum.TryParse<FieldType>(declared, ignoreCase: true, out var type) ? type : FieldType.String;

    private static RefSchema? Reference(JsonElement field)
        => String(field, "entity") is { Length: > 0 } target
            ? new RefSchema(
                target,
                Enum.TryParse<OnDelete>(String(field, "onDelete"), ignoreCase: true, out var onDelete)
                    ? onDelete
                    : OnDelete.Restrict)
            : null;

    private static IReadOnlyList<string>? Values(JsonElement field)
        => field.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
            ? [.. values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)]
            : null;

    private static string? String(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? Number(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}
