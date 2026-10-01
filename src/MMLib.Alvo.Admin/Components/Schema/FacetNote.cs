namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>What a save does to a facet the field editor draws no control for.</summary>
internal enum FacetFate
{
    /// <summary>Written back exactly as declared.</summary>
    Kept,

    /// <summary>Dropped: it belongs to what the field no longer is, or the operator asked.</summary>
    Removed,

    /// <summary>Written for the operator with the schema's own default, with no control to choose another.</summary>
    Written,
}

/// <summary>One facet the editor does not draw, what the save does to it, and the sentence that says so.</summary>
/// <remarks>
/// It exists because "nothing below is destroyed by an edit" was false (docs/todo-admin.md §8c, 5c lead): a
/// facet without a control was either rewritten from a stale prefill or silently carried. Now each one is said.
/// </remarks>
/// <param name="Facet">The key, as the descriptor spells it.</param>
/// <param name="Value">Its value as JSON — as declared, or as written for <see cref="FacetFate.Written"/>.</param>
/// <param name="Fate">What the save does to it.</param>
/// <param name="Reason">Why, in the words the editor shows beside it.</param>
internal sealed record FacetNote(string Facet, string Value, FacetFate Fate, string Reason);
