namespace MMLib.Alvo.Data;

/// <summary>One row of a batch update: which row, and the fields to change on it.</summary>
/// <remarks>
/// <b>A named type rather than a tuple</b>, because it appears in an implementor's signature and a tuple
/// element cannot carry documentation — and the thing that most needs documenting is that
/// <see cref="Values"/> is <em>partial</em>, exactly as <see cref="IAlvoData.UpdateAsync"/>'s is: a field
/// this dictionary does not mention keeps its stored value, and <c>WITH CHECK</c> is evaluated over the
/// complete post-image rather than over these values alone.
/// </remarks>
/// <param name="Id">The row to change.</param>
/// <param name="Values">The fields to change; a field this dictionary does not mention keeps its stored value.</param>
public sealed record AlvoRowPatch(Guid Id, IReadOnlyDictionary<string, object?> Values);
