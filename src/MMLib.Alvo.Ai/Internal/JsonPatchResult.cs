using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>What applying a patch produced: the new document and what it touched, or the one operation that failed.</summary>
/// <param name="Document">The patched copy; <see langword="null"/> when the patch failed.</param>
/// <param name="ChangedPaths">
/// The pointers the mutating operations addressed, distinct, in operation order: an append (<c>-</c>) as the index it
/// landed at, so it matches the pointer a validator reports; a <c>move</c> as its <c>from</c> and its <c>path</c>. A
/// <c>move</c> onto itself changes nothing and adds nothing, and neither does a <c>test</c>.
/// </param>
/// <param name="Error">Why the patch failed, or <see langword="null"/>.</param>
internal sealed record JsonPatchResult(JsonNode? Document, IReadOnlyList<string> ChangedPaths, JsonPatchError? Error)
{
    /// <summary>Whether every operation applied.</summary>
    internal bool Succeeded => Error is null;

    internal static JsonPatchResult Applied(JsonNode? document, IReadOnlyList<string> changedPaths) =>
        new(document, changedPaths, Error: null);

    internal static JsonPatchResult Refused(JsonPatchError error) => new(Document: null, [], error);
}

/// <summary>Why one operation — and therefore the whole patch (RFC 6902 §5) — failed.</summary>
/// <param name="Code">A stable slug a model can branch on.</param>
/// <param name="Op">The index of the failing operation, when one is to blame.</param>
/// <param name="Pointer">The pointer the operation wrote that could not be honoured.</param>
/// <param name="Message">What went wrong.</param>
/// <param name="Fix">What to do instead, when there is something concrete to say.</param>
internal sealed record JsonPatchError(string Code, int? Op, string Pointer, string Message, string? Fix)
{
    internal const string InvalidOperation = "invalid-operation";
    internal const string PathNotFound = "path-not-found";
    internal const string IndexOutOfRange = "index-out-of-range";
    internal const string NotAContainer = "not-a-container";
    internal const string MoveIntoItself = "move-into-itself";
    internal const string TestFailed = "test-failed";
    internal const string WholeDocumentReplace = "whole-document-replace";
    internal const string PatchTooLarge = "patch-too-large";
}
