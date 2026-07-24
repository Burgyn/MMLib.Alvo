using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

/// <summary>
/// Source-generated serialization context for <see cref="AlvoDescriptor"/> and its
/// nested model, backing <see cref="AlvoDescriptor.Parse"/> and
/// <see cref="AlvoDescriptor.Serialize"/>. Property names are camelCase to match the
/// schema; <see langword="null"/> members are omitted so optional/absent members
/// round-trip as absent.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(AlvoDescriptor))]
internal sealed partial class AlvoDescriptorJsonContext : JsonSerializerContext;
