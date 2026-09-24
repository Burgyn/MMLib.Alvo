using MMLib.Alvo.Schema;
using System.Text.Json;

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
            if (!document.RootElement.TryGetProperty("entities", out var entities)
                || !entities.TryGetProperty(entity, out var declared))
            {
                return null;
            }

            return new EntitySchema
            {
                Name = entity,
                Description = String(declared, "description"),
                Tenancy = string.Equals(String(declared, "tenancy"), "scoped", StringComparison.Ordinal)
                    ? TenancyMode.Scoped
                    : TenancyMode.Global,
                Audit = Flag(declared, "audit"),
                SoftDelete = Flag(declared, "softDelete"),
                Fields = Fields(declared),
            };
        }
    }

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
        })];
    }

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
