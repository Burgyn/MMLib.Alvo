using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// The question asked before a whole document replaces a working copy that holds unapplied edits: an import, or the
/// assistant's proposal taken to Preview (spec §3.2, final review I4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacing a dirty copy is discarding it.</b> <c>DiscardConfirm</c> already asks before the explicit Discard; the
/// same loss arrived through two other doors with no question at all. Both now ask in the same words, and a clean copy
/// is replaced at once, because there is nothing to lose.
/// </para>
/// <para>
/// Words here rather than a second shared component: <c>DiscardConfirm</c> is public, and a parameter for what replaces
/// the copy would grow the package's contract for a sentence.
/// </para>
/// </remarks>
internal static class CopyReplacement
{
    /// <summary>"Discard 3 unapplied changes?" — the count the pending bar shows.</summary>
    /// <param name="pending">How many changes the copy holds.</param>
    public static string Title(int pending)
        => $"Discard {pending.ToString(CultureInfo.InvariantCulture)} unapplied change{(pending == 1 ? string.Empty : "s")}?";

    /// <summary>What is lost, first, and what takes its place.</summary>
    /// <param name="replacement">What replaces the copy, as the subject of the sentence: "The pasted descriptor".</param>
    public static string Consequence(string replacement)
        => $"{replacement} replaces the working copy, across all screens, not only this one. What you staged in it is lost, and there is no undo.";

    /// <summary>Whether <paramref name="text"/> is a JSON object, the only thing a working copy can hold.</summary>
    /// <param name="text">The candidate descriptor.</param>
    public static bool IsObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
