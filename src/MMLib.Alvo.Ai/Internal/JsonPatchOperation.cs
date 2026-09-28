using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>One RFC 6902 operation, read and checked for the members its <c>op</c> requires.</summary>
internal sealed record JsonPatchOperation(int Index, string Op, JsonPointer Path, JsonPointer? From, JsonNode? Value)
{
    internal const string Add = "add";
    internal const string Remove = "remove";
    internal const string Replace = "replace";
    internal const string Move = "move";
    internal const string Copy = "copy";
    internal const string Test = "test";

    private static readonly HashSet<string> _known = new(StringComparer.Ordinal) { Add, Remove, Replace, Move, Copy, Test };

    /// <summary>Reads the operation at <paramref name="index"/>, or says why it is not one.</summary>
    internal static JsonPatchError? TryRead(JsonElement element, int index, out JsonPatchOperation? operation)
    {
        operation = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Malformed(index, string.Empty, "An operation is a JSON object with 'op' and 'path'.");
        }

        var op = StringMember(element, "op");
        if (op is null || !_known.Contains(op))
        {
            return Malformed(index, string.Empty, $"Unrecognized op '{op}'. An op is one of: add, remove, replace, move, copy, test.");
        }

        var pathText = StringMember(element, "path");
        return JsonPointer.TryParse(pathText, out var path)
            ? ReadOperands(element, index, op, path, out operation)
            : Malformed(index, pathText ?? string.Empty, "'path' must be a JSON Pointer: \"\" or a string starting with '/'.");
    }

    private static JsonPatchError? ReadOperands(
        JsonElement element, int index, string op, JsonPointer path, out JsonPatchOperation? operation)
    {
        operation = null;
        JsonPointer? from = null;
        if (op is Move or Copy && !JsonPointer.TryParse(StringMember(element, "from"), out from))
        {
            return Malformed(index, path.Text, $"'{op}' needs 'from', a JSON Pointer to the value to {op}.");
        }

        JsonNode? value = null;
        if (op is Add or Replace or Test && !TryValue(element, out value))
        {
            return Malformed(index, path.Text, $"'{op}' needs 'value'.");
        }

        operation = new JsonPatchOperation(index, op, path, from, value);
        return null;
    }

    private static bool TryValue(JsonElement element, out JsonNode? value)
    {
        value = null;
        if (!element.TryGetProperty("value", out var raw))
        {
            return false;
        }

        value = raw.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(raw.GetRawText());
        return true;
    }

    private static string? StringMember(JsonElement element, string name) =>
        element.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    private static JsonPatchError Malformed(int index, string pointer, string message) =>
        new(JsonPatchError.InvalidOperation, index, pointer, message, Fix: null);
}
