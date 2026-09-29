using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// What the record grid draws, worked out by the Data screen and cascaded rather than passed.
/// </summary>
/// <remarks>
/// Cascaded for <see cref="RecordFormScope"/>'s reason: a Razor component's parameters are public API, and
/// most of these are internal types a public parameter cannot carry. The grid's actions stay parameters,
/// because an <c>EventCallback</c> is what redraws the screen that owns the page after a sort or a step.
/// </remarks>
/// <param name="Page">The rows to draw.</param>
/// <param name="Columns">The columns the grid shows, in order.</param>
/// <param name="Masks">The entity's <c>hidden</c> fields, which cannot be sorted by.</param>
/// <param name="Label">The entity's own label, for a phone card's title.</param>
/// <param name="Labels">The label of each referenced row on the page, by target entity and id.</param>
/// <param name="Sort">The sort in force, or <see langword="null"/> for the entity's own order.</param>
/// <param name="HasPrevious">Whether there is a page before this one.</param>
/// <param name="SheetOpen">Whether a record's sheet is over the grid, so focus can return to its row on close.</param>
/// <param name="Created">The record the operator just created, to select and scroll to (spec §3.5).</param>
/// <param name="FocusRow">The row to give focus to once it is drawn, such as the one after a deleted record.</param>
/// <param name="Refusals">How many write refusals the screen has drawn: a new one takes focus, so the grid gives none back.</param>
internal sealed record RecordGridScope(
    AlvoPage Page,
    IReadOnlyList<FieldSchema> Columns,
    FieldMasks Masks,
    RowLabel? Label,
    IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, string>> Labels,
    GridSort? Sort,
    bool HasPrevious,
    bool SheetOpen,
    Guid? Created,
    RowFocus? FocusRow,
    int Refusals);

/// <summary>A row to focus, once; a new instance for every request, so the same row can be asked for twice.</summary>
/// <param name="Id">The row's record id.</param>
internal sealed record RowFocus(Guid Id);
