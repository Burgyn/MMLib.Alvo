using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

internal sealed class AlvoDescriptor
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("tenancy")]
    public TenancyDto? Tenancy { get; init; }

    [JsonPropertyName("entities")]
    public Dictionary<string, EntityDto> Entities { get; init; } = new();
}

internal sealed class TenancyDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

internal sealed class EntityDto
{
    [JsonPropertyName("renamedFrom")]
    public string? RenamedFrom { get; init; }

    [JsonPropertyName("storage")]
    public string? Storage { get; init; }

    [JsonPropertyName("tenancy")]
    public string? Tenancy { get; init; }

    [JsonPropertyName("softDelete")]
    public bool SoftDelete { get; init; }

    [JsonPropertyName("audit")]
    public bool Audit { get; init; }

    [JsonPropertyName("fields")]
    public Dictionary<string, FieldDto> Fields { get; init; } = new();

    [JsonPropertyName("indexes")]
    public List<IndexDto>? Indexes { get; init; }
}

internal sealed class FieldDto
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("renamedFrom")]
    public string? RenamedFrom { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("unique")]
    public bool Unique { get; init; }

    [JsonPropertyName("nullable")]
    public bool? Nullable { get; init; }

    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; init; }

    [JsonPropertyName("precision")]
    public int? Precision { get; init; }

    [JsonPropertyName("scale")]
    public int? Scale { get; init; }

    [JsonPropertyName("values")]
    public List<string>? Values { get; init; }

    [JsonPropertyName("entity")]
    public string? Entity { get; init; }

    [JsonPropertyName("onDelete")]
    public string? OnDelete { get; init; }

    [JsonPropertyName("index")]
    public bool Index { get; init; }

    [JsonPropertyName("computed")]
    public string? Computed { get; init; }
}

internal sealed class IndexDto
{
    [JsonPropertyName("fields")]
    public List<string> Fields { get; init; } = new();

    [JsonPropertyName("unique")]
    public bool Unique { get; init; }
}
