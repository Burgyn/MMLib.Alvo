using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Api.Internal;

/// <summary>Every refusal the batch body reader can produce, composed in one place.</summary>
/// <remarks>
/// <para>
/// One catalogue rather than a message per call site, for the reason <see cref="QueryViolations"/> gives:
/// a wording invented where it is thrown is a wording nobody compares with its siblings.
/// </para>
/// <para>
/// <b>The pointer convention is this layer's, not the port's.</b> <see cref="AlvoRowRefusal"/> carries an
/// <c>int</c> index because a port knows a row's position and not a JSON Pointer into a body it never sees;
/// <see cref="RowPointer"/> is where that index becomes <c>/rows/3</c>, beside the field pointers this file
/// already composes.
/// </para>
/// <para>
/// <b>No producer here interpolates caller-supplied text.</b> The only values that reach a message are
/// server-owned: a configured option's bound and the reserved member name.
/// </para>
/// </remarks>
internal static class BatchViolations
{
    /// <summary>The one member a batch body carries.</summary>
    internal const string RowsMember = "rows";

    /// <summary>The JSON Pointer to one row of the batch.</summary>
    /// <param name="index">The row's position, counting from zero.</param>
    internal static string RowPointer(int index) =>
        $"/{RowsMember}/{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The JSON Pointer to one field of one row.</summary>
    /// <remarks>
    /// Composed from the row's index and the pointer <see cref="PayloadViolations.PointerTo"/> already
    /// produced for a single write, so a batch's pointer is a single write's pointer with a prefix — and a
    /// caller who can resolve one can resolve the other.
    /// </remarks>
    /// <param name="index">The row's position, counting from zero.</param>
    /// <param name="fieldPointer">The pointer a single write would have produced for this field.</param>
    internal static string FieldPointer(int index, string fieldPointer) => RowPointer(index) + fieldPointer;

    /// <summary>A body that is an object but carries no <c>rows</c> array.</summary>
    internal static AlvoViolation NotABatch() => new(
        PayloadViolations.BodyPointer,
        "not-a-batch",
        $"The request body must be an object carrying a '{RowsMember}' array.",
        $"Send {{\"{RowsMember}\": [ … ]}}. Each element is one row, in the shape the single-row route takes.");

    /// <summary>An empty <c>rows</c> array.</summary>
    /// <remarks>
    /// Refused rather than answered as a write of nothing, and on a <c>DELETE</c> that is the point: RFC 9110
    /// §9.3.5 leaves a delete's body undefined, so an intermediary may strip it — and a stripped body read as
    /// "no rows to delete" would be a silent success for a request that never arrived.
    /// </remarks>
    internal static AlvoViolation EmptyBatch() => new(
        PayloadViolations.PointerTo(RowsMember),
        "empty-batch",
        "A batch must carry at least one row.",
        "Send at least one row. An empty batch is refused rather than answered as a write of nothing, "
        + "because a body an intermediary stripped would otherwise look like a success.");

    /// <summary>A batch past <see cref="AlvoApiOptions.MaxBatchRows"/>.</summary>
    /// <param name="max">The configured bound.</param>
    internal static AlvoViolation TooManyRows(int max) => new(
        PayloadViolations.PointerTo(RowsMember),
        "batch-too-many-rows",
        $"A batch may carry at most {max.ToString(System.Globalization.CultureInfo.InvariantCulture)} rows.",
        "Split the batch. Each part is still one transaction, so a part that fails leaves its own rows "
        + "unwritten while the parts that succeeded stay written.");

    /// <summary>A <c>rows</c> element that is not an object.</summary>
    /// <param name="index">The row's position, counting from zero.</param>
    internal static AlvoViolation RowIsNotAnObject(int index) => new(
        RowPointer(index),
        "not-an-object",
        "Each row must be a JSON object.",
        "Send an object per row, in the shape the single-row route takes.");

    /// <summary>A row of a batch update or delete whose <c>id</c> is absent or not a uuid.</summary>
    /// <param name="index">The row's position, counting from zero.</param>
    internal static AlvoViolation RowIdIsNotAUuid(int index) => new(
        FieldPointer(index, PayloadViolations.PointerTo(AlvoManagedColumns.Id)),
        "invalid-row-id",
        "Each row must name the row it changes with an 'id' that is a uuid.",
        "Send the 'id' exactly as a previous response returned it.");

    /// <summary>The refusal a port row refusal becomes on this surface.</summary>
    /// <remarks>
    /// The port's <c>Code</c>, <c>Message</c> and <c>FixSuggestion</c> travel unchanged — they are already
    /// server-owned by that type's own contract — and only the pointer is composed here, because only this
    /// layer knows the body the index refers to.
    /// </remarks>
    /// <param name="refusal">The refusal the port produced.</param>
    internal static AlvoViolation FromPort(AlvoRowRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return new AlvoViolation(
            RowPointer(refusal.Index), refusal.Code, refusal.Message, refusal.FixSuggestion);
    }
}
