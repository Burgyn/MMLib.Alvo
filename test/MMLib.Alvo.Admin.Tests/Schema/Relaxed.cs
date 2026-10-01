using System.Text.Encodings.Web;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>How the working copy writes JSON (<c>WorkingCopy._pretty</c>'s encoder): a bare <c>ToJsonString()</c> escapes <c>+</c> and <c>'</c>.</summary>
internal static class Relaxed
{
    /// <summary>Compact, with the working copy's encoder.</summary>
    public static JsonSerializerOptions Options { get; } = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
