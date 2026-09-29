using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>How a tool result is written for the model.</summary>
/// <remarks>
/// <para>
/// camelCase and no indentation: the model reads JSON the same way every other Alvo client does, and
/// whitespace in a tool result is tokens nobody is paying for on purpose.
/// </para>
/// <para>
/// <b>Relaxed escaping, deliberately.</b> The default encoder writes <c>č</c> as <c>\u010D</c> and <c>'</c> as
/// <c>\u0027</c>, and a model that must reproduce a descriptor of escape sequences hallucinates hex digits — the
/// <c>0x01</c> of the live failure. A tool result goes only to the model, never into HTML; the drawer renders
/// refusals as Razor-encoded text, so <c>&lt;</c> and <c>&gt;</c> left unescaped reach no browser.
/// </para>
/// </remarks>
internal static class ToolJson
{
    /// <summary>The options every tool result is written with.</summary>
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
