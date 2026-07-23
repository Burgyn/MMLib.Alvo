using System.Text.Json;

namespace MMLib.Alvo.Descriptor;

internal static class DescriptorParser
{
    public static AlvoDescriptor Parse(string json)
        => JsonSerializer.Deserialize(json, DescriptorJsonContext.Default.AlvoDescriptor)
           ?? throw new InvalidDataException("Descriptor JSON deserialized to null.");
}
