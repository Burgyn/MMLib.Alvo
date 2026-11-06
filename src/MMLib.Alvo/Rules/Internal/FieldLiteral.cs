using MMLib.Alvo.Expressions;

using System.Text.Json;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>A JSON literal read as the value a field holds — what a before-hook's <c>mutate</c> and an <c>entity.update</c>'s payload are checked with at apply.</summary>
internal static class FieldLiteral
{
    /// <summary>
    /// A JSON literal in the representation a field of <paramref name="declared"/> holds, or
    /// <see langword="null"/> when the literal's shape does not fit that type at all.
    /// </summary>
    /// <remarks>
    /// A timestamp and a UUID are parsed here rather than passed through as text: the storage driver would
    /// convert them anyway, and parsing at apply is what turns <c>"not-a-date"</c> into an authoring error.
    /// </remarks>
    internal static object? Convert(JsonElement json, CelValueType declared) => (declared, json.ValueKind) switch
    {
        (CelValueType.String, JsonValueKind.String) => json.GetString(),
        (CelValueType.Bool, JsonValueKind.True or JsonValueKind.False) => json.GetBoolean(),
        (CelValueType.Int, JsonValueKind.Number) => json.TryGetInt64(out var integer) ? integer : null,
        (CelValueType.Decimal, JsonValueKind.Number) => json.TryGetDecimal(out var number) ? number : null,
        (CelValueType.Timestamp, JsonValueKind.String) =>
            json.TryGetDateTimeOffset(out var instant) ? instant : null,
        (CelValueType.Uuid, JsonValueKind.String) => json.TryGetGuid(out var id) ? id : null,
        _ => null,
    };
}
