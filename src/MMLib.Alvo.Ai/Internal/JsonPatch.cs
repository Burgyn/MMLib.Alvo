using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>
/// RFC 6902 JSON Patch over <see cref="System.Text.Json.Nodes"/> — applied atomically, to a copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-house rather than a package</b>: <c>Microsoft.AspNetCore.JsonPatch.SystemTextJson</c> targets typed POCOs and
/// would pull ASP.NET Core into a package whose only reference is Abstractions. Conformance is the community
/// <c>json-patch-tests</c> suite, vendored under <c>MMLib.Alvo.Ai.Tests/TestData</c>.
/// </para>
/// <para>
/// <b>RFC-pure, with one bound of its own.</b> What the assistant's tools refuse on top — the root pointer, the
/// operation count — is <see cref="PatchAdmission"/>'s, because the RFC admits both and the suite tests them.
/// </para>
/// <para>
/// <b>The added-bytes budget is the engine's</b>, because only the engine sees the document a <c>copy</c> reads:
/// every operation's <c>value</c> and every copied subtree, measured by <see cref="PatchBytes"/> before it is cloned,
/// draw on one budget of <see cref="PatchAdmission.MaximumValueBytes"/>. Past it the patch fails as
/// <c>patch-too-large</c>; twenty self-doubling copies are a refusal, not a 25 MB document.
/// </para>
/// </remarks>
internal static class JsonPatch
{
    private const string AppendToken = "-";
    private const int MaximumListedKeys = 20;
    private const string InsertIndexFix = "Use an index from 0 to the item count, or '-' to append.";

    /// <summary>Applies <paramref name="operations"/> to a copy of <paramref name="document"/>; any failure aborts all.</summary>
    internal static JsonPatchResult Apply(JsonNode? document, JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array)
        {
            return JsonPatchResult.Refused(new JsonPatchError(
                JsonPatchError.InvalidOperation, Op: null, string.Empty, "A patch is a JSON array of operations.", Fix: null));
        }

        var working = document?.DeepClone();
        var changed = new List<string>();
        var budget = new AddedBytes();
        var index = 0;
        foreach (var element in operations.EnumerateArray())
        {
            var step = Step(working, element, index++, budget);
            if (step.Error is { } error)
            {
                return JsonPatchResult.Refused(error);
            }

            working = step.Document;
            changed.AddRange(step.Changed);
        }

        return JsonPatchResult.Applied(working, [.. changed.Distinct(StringComparer.Ordinal)]);
    }

    private static StepResult Step(JsonNode? document, JsonElement element, int index, AddedBytes budget)
    {
        if (JsonPatchOperation.TryRead(element, index, out var operation) is { } malformed)
        {
            return StepResult.Failed(malformed);
        }

        return budget.TrySpend(PatchBytes.OfValue(element))
            ? Dispatch(document, operation!, budget)
            : StepResult.Failed(ValuesTooLarge(operation!));
    }

    private static StepResult Dispatch(JsonNode? document, JsonPatchOperation operation, AddedBytes budget) =>
        operation.Op switch
        {
            JsonPatchOperation.Add => Add(document, operation, operation.Path, operation.Value),
            JsonPatchOperation.Remove => Remove(document, operation),
            JsonPatchOperation.Replace => Replace(document, operation),
            JsonPatchOperation.Move => Move(document, operation),
            JsonPatchOperation.Copy => Copy(document, operation, budget),
            _ => Test(document, operation),
        };

    private static StepResult Add(JsonNode? document, JsonPatchOperation operation, JsonPointer path, JsonNode? value)
    {
        if (path.IsRoot)
        {
            return StepResult.Done(value, path.Text);
        }

        if (!TryResolve(document, path.Parent, out var parent))
        {
            return StepResult.Failed(Missing(operation, path, document));
        }

        return parent switch
        {
            JsonObject members => SetMember(document, members, path, value),
            JsonArray items => InsertItem(document, items, operation, path, value),
            _ => StepResult.Failed(NotAContainer(operation, path)),
        };
    }

    private static StepResult SetMember(JsonNode? document, JsonObject members, JsonPointer path, JsonNode? value)
    {
        members[path.Last] = value;
        return StepResult.Done(document, path.Text);
    }

    private static StepResult InsertItem(
        JsonNode? document, JsonArray items, JsonPatchOperation operation, JsonPointer path, JsonNode? value)
    {
        if (path.Last == AppendToken)
        {
            var appendedAt = items.Count;
            items.Add(value);
            return StepResult.Done(document, path.Parent.Append(appendedAt.ToString(CultureInfo.InvariantCulture)).Text);
        }

        if (!TryIndex(path.Last, items.Count + 1, out var index))
        {
            return StepResult.Failed(IndexOutOfRange(operation, path, items.Count, InsertIndexFix));
        }

        items.Insert(index, value);
        return StepResult.Done(document, path.Text);
    }

    private static StepResult Remove(JsonNode? document, JsonPatchOperation operation)
    {
        var path = operation.Path;
        if (path.IsRoot)
        {
            return StepResult.Done(null, path.Text);
        }

        if (!TryResolve(document, path.Parent, out var parent))
        {
            return StepResult.Failed(Missing(operation, path, document));
        }

        return parent switch
        {
            JsonObject members => RemoveMember(document, members, operation),
            JsonArray items => RemoveItem(document, items, operation),
            _ => StepResult.Failed(NotAContainer(operation, path)),
        };
    }

    private static StepResult RemoveMember(JsonNode? document, JsonObject members, JsonPatchOperation operation) =>
        members.Remove(operation.Path.Last)
            ? StepResult.Done(document, operation.Path.Text)
            : StepResult.Failed(Missing(operation, operation.Path, document));

    private static StepResult RemoveItem(JsonNode? document, JsonArray items, JsonPatchOperation operation)
    {
        if (!TryIndex(operation.Path.Last, items.Count, out var index))
        {
            return StepResult.Failed(ExistingItemOutOfRange(operation, items.Count));
        }

        items.RemoveAt(index);
        return StepResult.Done(document, operation.Path.Text);
    }

    private static StepResult Replace(JsonNode? document, JsonPatchOperation operation)
    {
        var path = operation.Path;
        if (path.IsRoot)
        {
            return StepResult.Done(operation.Value, path.Text);
        }

        if (!TryResolve(document, path.Parent, out var parent))
        {
            return StepResult.Failed(Missing(operation, path, document));
        }

        return parent switch
        {
            JsonObject members => ReplaceMember(document, members, operation),
            JsonArray items => ReplaceItem(document, items, operation),
            _ => StepResult.Failed(NotAContainer(operation, path)),
        };
    }

    private static StepResult ReplaceMember(JsonNode? document, JsonObject members, JsonPatchOperation operation) =>
        members.ContainsKey(operation.Path.Last)
            ? SetMember(document, members, operation.Path, operation.Value)
            : StepResult.Failed(Missing(operation, operation.Path, document));

    private static StepResult ReplaceItem(JsonNode? document, JsonArray items, JsonPatchOperation operation)
    {
        if (!TryIndex(operation.Path.Last, items.Count, out var index))
        {
            return StepResult.Failed(ExistingItemOutOfRange(operation, items.Count));
        }

        items.RemoveAt(index);
        items.Insert(index, operation.Value);
        return StepResult.Done(document, operation.Path.Text);
    }

    private static StepResult Move(JsonNode? document, JsonPatchOperation operation)
    {
        var from = operation.From!;
        if (string.Equals(from.Text, operation.Path.Text, StringComparison.Ordinal))
        {
            return TryResolve(document, from, out _) ? StepResult.Done(document) : StepResult.Failed(Missing(operation, from, document));
        }

        if (from.IsProperPrefixOf(operation.Path))
        {
            return StepResult.Failed(MoveIntoItself(operation));
        }

        if (!TryDetach(document, from, out var moved))
        {
            return StepResult.Failed(Missing(operation, from, document));
        }

        var added = Add(document, operation, operation.Path, moved);
        return added.Error is null ? added with { Changed = [from.Text, .. added.Changed] } : added;
    }

    private static StepResult Copy(JsonNode? document, JsonPatchOperation operation, AddedBytes budget)
    {
        if (!TryResolve(document, operation.From!, out var source))
        {
            return StepResult.Failed(Missing(operation, operation.From!, document));
        }

        var copiedBytes = PatchBytes.Of(source);
        return budget.TrySpend(copiedBytes)
            ? Add(document, operation, operation.Path, source?.DeepClone())
            : StepResult.Failed(CopyTooLarge(operation, copiedBytes));
    }

    private static StepResult Test(JsonNode? document, JsonPatchOperation operation)
    {
        if (!TryResolve(document, operation.Path, out var actual))
        {
            return StepResult.Failed(Missing(operation, operation.Path, document));
        }

        return JsonNode.DeepEquals(actual, operation.Value) ? StepResult.Done(document) : StepResult.Failed(TestFailed(operation));
    }

    private static bool TryResolve(JsonNode? document, JsonPointer pointer, out JsonNode? node)
    {
        node = document;
        foreach (var token in pointer.Tokens)
        {
            if (!TryChild(node, token, out node))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryChild(JsonNode? node, string token, out JsonNode? child)
    {
        child = null;
        return node switch
        {
            JsonObject members => members.TryGetPropertyValue(token, out child),
            JsonArray items => TryItem(items, token, out child),
            _ => false,
        };
    }

    private static bool TryItem(JsonArray items, string token, out JsonNode? item)
    {
        item = null;
        if (!TryIndex(token, items.Count, out var index))
        {
            return false;
        }

        item = items[index];
        return true;
    }

    private static bool TryDetach(JsonNode? document, JsonPointer path, out JsonNode? removed)
    {
        removed = null;
        if (!TryResolve(document, path.Parent, out var parent))
        {
            return false;
        }

        switch (parent)
        {
            case JsonObject members when members.TryGetPropertyValue(path.Last, out removed):
                members.Remove(path.Last);
                return true;
            case JsonArray items when TryIndex(path.Last, items.Count, out var index):
                removed = items[index];
                items.RemoveAt(index);
                return true;
            default:
                return false;
        }
    }

    private static bool TryIndex(string token, int exclusiveUpperBound, out int index)
    {
        index = -1;
        var canonical = token == "0" || (token.Length > 0 && token[0] != '0' && token.All(char.IsAsciiDigit));

        return canonical
            && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index < exclusiveUpperBound;
    }

    private static JsonPatchError Missing(JsonPatchOperation operation, JsonPointer pointer, JsonNode? document)
    {
        var (reached, node) = DeepestExisting(document, pointer);

        return new JsonPatchError(
            JsonPatchError.PathNotFound,
            operation.Index,
            pointer.Text,
            $"'{pointer.Text}' does not exist in the document.",
            SiblingsHint(reached, node));
    }

    private static (JsonPointer Reached, JsonNode? Node) DeepestExisting(JsonNode? document, JsonPointer pointer)
    {
        var reached = JsonPointer.Root;
        var node = document;
        foreach (var token in pointer.Tokens)
        {
            if (!TryChild(node, token, out var child))
            {
                break;
            }

            reached = reached.Append(token);
            node = child;
        }

        return (reached, node);
    }

    private static string? SiblingsHint(JsonPointer reached, JsonNode? node) =>
        node is JsonObject { Count: > 0 } members
            ? $"{Display(reached)} has: {string.Join(", ", members.Select(member => member.Key).Take(MaximumListedKeys))}."
            : null;

    private static string Display(JsonPointer pointer) => pointer.IsRoot ? "The document" : $"'{pointer.Text}'";

    private static JsonPatchError NotAContainer(JsonPatchOperation operation, JsonPointer path) => new(
        JsonPatchError.NotAContainer, operation.Index, path.Text,
        $"'{path.Parent.Text}' is neither an object nor an array, so it cannot hold '{path.Last}'.", Fix: null);

    private static JsonPatchError IndexOutOfRange(
        JsonPatchOperation operation, JsonPointer path, int count, string fix) => new(
        JsonPatchError.IndexOutOfRange, operation.Index, path.Text,
        $"'{path.Last}' is not an index into the array at '{path.Parent.Text}', which has {count} items.", fix);

    private static JsonPatchError ExistingItemOutOfRange(JsonPatchOperation operation, int count) =>
        IndexOutOfRange(operation, operation.Path, count, count == 0
            ? $"The array is empty, so there is no item to {operation.Op}."
            : $"Use an index from 0 to {count - 1}; '-' names no existing item.");

    private static JsonPatchError ValuesTooLarge(JsonPatchOperation operation) => new(
        JsonPatchError.PatchTooLarge, operation.Index, operation.Path.Text,
        $"This operation's value takes what the patch adds past {PatchAdmission.MaximumValueBytes} bytes.",
        "Send only what the request changes; untouched parts of the descriptor never need to be sent.");

    private static JsonPatchError CopyTooLarge(JsonPatchOperation operation, long copiedBytes) => new(
        JsonPatchError.PatchTooLarge, operation.Index, operation.Path.Text,
        $"Copying '{operation.From!.Text}' adds {copiedBytes} bytes, which takes what the patch adds past " +
        $"{PatchAdmission.MaximumValueBytes} bytes.",
        "Copy a smaller subtree, or add only the part the request needs.");

    private static JsonPatchError MoveIntoItself(JsonPatchOperation operation) => new(
        JsonPatchError.MoveIntoItself, operation.Index, operation.Path.Text,
        $"'{operation.From!.Text}' cannot be moved into '{operation.Path.Text}', which is inside it.", Fix: null);

    private static JsonPatchError TestFailed(JsonPatchOperation operation) => new(
        JsonPatchError.TestFailed, operation.Index, operation.Path.Text,
        $"The value at '{operation.Path.Text}' is not the one the 'test' operation expected.",
        "Call get_descriptor again; the document is not what this patch assumed.");

    private sealed class AddedBytes
    {
        private long _remaining = PatchAdmission.MaximumValueBytes;

        internal bool TrySpend(long bytes)
        {
            if (bytes > _remaining)
            {
                return false;
            }

            _remaining -= bytes;
            return true;
        }
    }

    private sealed record StepResult(JsonNode? Document, IReadOnlyList<string> Changed, JsonPatchError? Error)
    {
        internal static StepResult Done(JsonNode? document, params string[] changed) => new(document, changed, Error: null);

        internal static StepResult Failed(JsonPatchError error) => new(Document: null, [], error);
    }
}
