using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>How a tool result is written for the model.</summary>
/// <remarks>
/// camelCase and no indentation: the model reads JSON the same way every other Alvo client does, and
/// whitespace in a tool result is tokens nobody is paying for on purpose.
/// </remarks>
internal static class ToolJson
{
    /// <summary>The options every tool result is written with.</summary>
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
