using MMLib.Alvo.Ai.Internal;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>Where two descriptors differ, as RFC 6901 pointers — the eval's own account, not the tool's claim.</summary>
/// <remarks>
/// Pointers are built and read through the assistant's own <see cref="JsonPointer"/>, so the eval and the tool agree on
/// escaping by construction rather than by two copies of it.
/// </remarks>
internal static class DescriptorDiff
{
    /// <summary>Every member added, removed or changed, at the deepest object it differs in; an array as a whole.</summary>
    /// <param name="before">The descriptor the turn started from.</param>
    /// <param name="after">The descriptor it proposed.</param>
    internal static IReadOnlyList<string> Paths(string before, string after)
    {
        var paths = new List<string>();
        Walk(JsonNode.Parse(before), JsonNode.Parse(after), JsonPointer.Root, paths);
        return paths;
    }

    /// <summary>
    /// <paramref name="before"/> patched with <paramref name="operations"/> by the assistant's own JSON Patch, the way a
    /// proposal is made from a patch; throws when the patch does not apply.
    /// </summary>
    /// <param name="before">The descriptor to patch.</param>
    /// <param name="operations">An RFC 6902 operations array.</param>
    internal static string Patched(string before, JsonElement operations)
    {
        var result = JsonPatch.Apply(JsonNode.Parse(before), operations);
        return result.Document?.ToJsonString() ?? throw new InvalidOperationException($"The patch does not apply: {result.Error}");
    }

    /// <summary>The node <paramref name="pointer"/> addresses, or <see langword="null"/> when there is none.</summary>
    /// <param name="document">The document to read.</param>
    /// <param name="pointer">An RFC 6901 pointer.</param>
    internal static JsonNode? At(JsonNode? document, string pointer) =>
        JsonPointer.TryParse(pointer, out var parsed) ? parsed.Tokens.Aggregate(document, Child) : null;

    private static JsonNode? Child(JsonNode? node, string token) => node switch
    {
        JsonObject members => members[token],
        JsonArray items when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < items.Count => items[index],
        _ => null,
    };

    private static void Walk(JsonNode? before, JsonNode? after, JsonPointer pointer, List<string> paths)
    {
        if (before is JsonObject left && after is JsonObject right)
        {
            WalkMembers(left, right, pointer, paths);
        }
        else if (!JsonNode.DeepEquals(before, after))
        {
            paths.Add(pointer.Text);
        }
    }

    private static void WalkMembers(JsonObject left, JsonObject right, JsonPointer pointer, List<string> paths)
    {
        foreach (var key in left.Select(member => member.Key).Union(right.Select(member => member.Key), StringComparer.Ordinal))
        {
            var child = pointer.Append(key);
            if (left.ContainsKey(key) != right.ContainsKey(key))
            {
                paths.Add(child.Text);
            }
            else
            {
                Walk(left[key], right[key], child, paths);
            }
        }
    }
}
