using System.Text.Json.Serialization;

namespace MMLib.Alvo.Descriptor;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(AlvoDescriptor))]
internal sealed partial class DescriptorJsonContext : JsonSerializerContext;
