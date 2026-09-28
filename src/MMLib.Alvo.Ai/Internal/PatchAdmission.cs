using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// What the assistant's tools refuse in a patch before it runs: the whole document as a target, and size.
/// </summary>
/// <remarks>
/// <b>The root pointer is the removed <c>validate_descriptor</c> in disguise.</b> A <c>replace</c> at <c>""</c> is a
/// hand-retyped descriptor again — the failure this design exists to remove — so it is refused rather than admitted
/// as the RFC would. The bounds make a runaway model a refusal instead of a 30 KB dry run.
/// </remarks>
internal static class PatchAdmission
{
    /// <summary>The most operations one patch may carry.</summary>
    internal const int MaximumOperations = 50;

    /// <summary>The most UTF-8 bytes the operations' values may total.</summary>
    internal const int MaximumValueBytes = 16 * 1024;

    private static readonly HashSet<string> _mutating = new(StringComparer.Ordinal)
    {
        JsonPatchOperation.Add, JsonPatchOperation.Remove, JsonPatchOperation.Replace,
        JsonPatchOperation.Move, JsonPatchOperation.Copy,
    };

    /// <summary>The refusal for <paramref name="operations"/>, or <see langword="null"/> when it may run.</summary>
    internal static JsonPatchError? Check(JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var count = operations.GetArrayLength();
        return count > MaximumOperations ? TooMany(count) : CheckEach(operations);
    }

    private static JsonPatchError? CheckEach(JsonElement operations)
    {
        var index = 0;
        var valueBytes = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            if (TargetsWholeDocument(operation))
            {
                return WholeDocument(index);
            }

            valueBytes += ValueBytes(operation);
            index++;
        }

        return valueBytes > MaximumValueBytes ? TooLarge(valueBytes) : null;
    }

    private static bool TargetsWholeDocument(JsonElement operation) =>
        operation.ValueKind == JsonValueKind.Object
        && operation.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String
        && _mutating.Contains(op.GetString()!)
        && operation.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
        && path.GetString()!.Length == 0;

    private static int ValueBytes(JsonElement operation) =>
        operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("value", out var value)
            ? Encoding.UTF8.GetByteCount(value.GetRawText())
            : 0;

    private static JsonPatchError WholeDocument(int index) => new(
        JsonPatchError.WholeDocumentReplace, index, string.Empty,
        "An operation on the whole document (path \"\") is refused: it replaces the descriptor wholesale.",
        "Address the subtree the request changes, e.g. /entities/<entity> or /entities/<entity>/fields/<field>.");

    private static JsonPatchError TooMany(int count) => new(
        JsonPatchError.PatchTooLarge, Op: null, string.Empty,
        $"The patch has {count} operations; at most {MaximumOperations} are accepted.",
        "Replace one subtree, such as /entities/<entity>, instead of many small parts of it.");

    private static JsonPatchError TooLarge(int bytes) => new(
        JsonPatchError.PatchTooLarge, Op: null, string.Empty,
        $"The patch's values total {bytes} bytes; at most {MaximumValueBytes} are accepted.",
        "Send only what the request changes; untouched parts of the descriptor never need to be sent.");
}
