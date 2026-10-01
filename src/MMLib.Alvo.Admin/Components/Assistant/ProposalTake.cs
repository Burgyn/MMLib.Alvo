using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Components.Assistant;

/// <summary>
/// Puts an assistant's proposal in the working copy, or says why it did not — the drawer's half of the guard
/// <see cref="WorkingCopy.Replace"/> holds (docs/todo-admin.md §8d item 42).
/// </summary>
/// <remarks>
/// The assistant is fed a validated draft, so a refusal here is not expected; the point is that when the copy refuses,
/// the drawer says so in place rather than suggesting a reason for edits that never landed and opening a Preview of
/// whatever the copy already held. Out of the component so it can be tested (F-13).
/// </remarks>
internal static class ProposalTake
{
    /// <summary>The title of the refusal the drawer shows.</summary>
    public const string RefusedTitle = "That proposal could not be taken to Preview";

    /// <summary>The refusal's sentence, for a proposal the copy would not hold.</summary>
    public const string NotAnObject =
        "The proposal is not a JSON object, so the working copy was left as it was. Ask again, or edit the schema yourself.";

    /// <summary>Replaces the copy with the proposal and records the reason, or leaves both alone.</summary>
    /// <param name="copy">The operator's working copy.</param>
    /// <param name="descriptorJson">The proposed descriptor.</param>
    /// <param name="reason">What the apply should say it was for.</param>
    /// <returns>Why it was not taken, or <see langword="null"/> when the copy now holds it.</returns>
    public static string? Into(WorkingCopy copy, string descriptorJson, string? reason)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (!copy.Replace(descriptorJson))
        {
            return NotAnObject;
        }

        copy.SuggestReason(reason);
        return null;
    }
}
