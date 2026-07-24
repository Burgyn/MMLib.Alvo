using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// The typed, round-trippable model of an Alvo project descriptor — the declarative
/// definition of a backend (entities, rules, automation, auth, branding). This mirrors
/// <c>schema/project.schema.json</c> and is the read/write API every descriptor consumer
/// shares: the admin dashboard, the Management API, the CLI, MCP, and codegen/tooling.
/// </summary>
/// <remarks>
/// Round-trip fidelity is a guarantee: extension keys (<c>x-*</c>) and any members not
/// modelled here are preserved through <see cref="Parse"/> and <see cref="Serialize"/>.
/// Exact-byte identity is not guaranteed (member order and whitespace may change);
/// semantic content is.
/// </remarks>
public sealed record AlvoDescriptor
{
    /// <summary>Optional editor hint pointing at the schema; ignored by Alvo.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    /// <summary>Descriptor format version (<c>alvo.dev/v1</c>).</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Project identifier (kebab-case).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of this project/backend.</summary>
    public string? Description { get; init; }

    /// <summary>Identity of this project/backend (display name, logo).</summary>
    public Branding? Branding { get; init; }

    /// <summary>Content revision counter, incremented on every applied change; used for optimistic concurrency.</summary>
    public int? Revision { get; init; }

    /// <summary>Declares this backend as multi-tenant.</summary>
    public Tenancy? Tenancy { get; init; }

    /// <summary>Governance for runtime, user-defined entities.</summary>
    public DynamicEntities? DynamicEntities { get; init; }

    /// <summary>Authentication providers and application roles.</summary>
    public Auth? Auth { get; init; }

    /// <summary>Who may manage this project in the admin dashboard.</summary>
    public Access? Access { get; init; }

    /// <summary>Entity definitions, keyed by entity name.</summary>
    public required IReadOnlyDictionary<string, EntityDescriptor> Entities { get; init; }

    /// <summary>Event–condition–action automation rules, keyed by rule name.</summary>
    public IReadOnlyDictionary<string, AutomationRule>? Automation { get; init; }

    /// <summary>Reusable message templates, keyed by template name.</summary>
    public IReadOnlyDictionary<string, MessageTemplate>? Templates { get; init; }

    /// <summary>Reusable named validation formats, keyed by format name.</summary>
    public IReadOnlyDictionary<string, NamedFormat>? Formats { get; init; }

    /// <summary>Managed webhook endpoints.</summary>
    public Webhooks? Webhooks { get; init; }

    /// <summary>Custom logic functions, keyed by function name.</summary>
    public IReadOnlyDictionary<string, FunctionDescriptor>? Functions { get; init; }

    /// <summary>Extension keys (<c>x-*</c>) preserved verbatim through apply and export.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Parses descriptor JSON into an <see cref="AlvoDescriptor"/>.</summary>
    /// <param name="json">The descriptor JSON text.</param>
    /// <returns>The parsed descriptor.</returns>
    /// <exception cref="System.Text.Json.JsonException">The JSON is malformed or violates the model's shape.</exception>
    /// <exception cref="InvalidOperationException">The JSON deserialized to <see langword="null"/>.</exception>
    public static AlvoDescriptor Parse(string json)
        => JsonSerializer.Deserialize(json, AlvoDescriptorJsonContext.Default.AlvoDescriptor)
           ?? throw new InvalidOperationException("Descriptor JSON deserialized to null.");

    /// <summary>Serializes an <see cref="AlvoDescriptor"/> back to descriptor JSON.</summary>
    /// <param name="descriptor">The descriptor to serialize.</param>
    /// <returns>The descriptor JSON text (indented, camelCase property names).</returns>
    public static string Serialize(AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.Serialize(descriptor, AlvoDescriptorJsonContext.Default.AlvoDescriptor);
    }
}
