using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// What the record form needs from the Data screen beyond the entity's shape, cascaded rather than passed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cascaded, because a Razor component's parameters are public API.</b> Each of these would be a new
/// public parameter of an internal type, which C# does not allow, or of a public one the package would
/// then have to keep. The Data screen works all of it out once per entity for its grid anyway.
/// </para>
/// <para>
/// <b><see cref="Report"/> is how a write says what it did</b> — "Saved SO-2026-0163" — on the screen
/// underneath, after the sheet has closed. An error is never reported this way: it stays in the sheet,
/// where the operator can act on it (design §5.5, errors are not toasts).
/// </para>
/// </remarks>
/// <param name="Schema">The applied schema, for the targets of the entity's references.</param>
/// <param name="Label">The entity's own label, for the sheet's title and the report.</param>
/// <param name="Masks">The entity's <c>hidden</c> fields.</param>
/// <param name="Locks">The entity's <c>readOnly</c> fields.</param>
/// <param name="Targets">The label of each entity a reference points at, by entity; absent when it has none.</param>
/// <param name="Report">Says what a successful write did, in one sentence.</param>
internal sealed record RecordFormScope(
    SchemaModel Schema,
    RowLabel? Label,
    FieldMasks Masks,
    FieldLocks Locks,
    IReadOnlyDictionary<string, RowLabel> Targets,
    Action<string> Report);
