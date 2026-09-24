using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>One row a reference control offers: the id it sets, and the label it reads as.</summary>
/// <param name="Id">The target row's id.</param>
/// <param name="Label">The target row's label, else its short id.</param>
internal sealed record RefOption(Guid Id, string Label);

/// <summary>
/// The record form's reference control: a text box that searches the target's labelled rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usability test T3 is the reason.</b> "Move this order to another bike" was a workaround — copy a
/// uuid out of another table — because a reference was a raw id box. The control searches the target
/// by its label (the same heuristic the grid's cells use, <see cref="RefLabels.For"/>) and lists label and
/// short id; choosing one sets the id.
/// </para>
/// <para>
/// <b>Through the data port as the operator</b>, like the grid's labels: an option is only ever a row this
/// caller could have listed through <c>/api</c>. The search is the grid's own <c>ilike</c>
/// (<see cref="GridQuery.Search"/>) once per word, all words required, so "ada love" finds Ada Lovelace
/// when the label is <c>first_name</c> + <c>last_name</c>.
/// </para>
/// <para>
/// <b>A pasted id is always accepted.</b> A target with no label, a label field masked from this caller,
/// or a row the search does not reach still leaves the operator a way to set the reference: a full uuid
/// typed into the box sets it, and the engine decides whether it resolves.
/// </para>
/// <para>
/// <b>It searches only the label's <see cref="RowLabel.Searchable"/> fields.</b> A label field hidden by a CEL
/// mask would have the port refuse every search for a caller it hides from, and the control reported that as
/// "Nothing matches" — which reads as "no such row" rather than "you cannot search this". A label whose every
/// field is so masked is not searchable at all, and the box asks for a pasted id instead.
/// </para>
/// </remarks>
internal sealed class RefPicker
{
    /// <summary>How many rows one search lists.</summary>
    public const int Limit = 20;

    /// <summary>How many words of a search are matched; the rest are ignored.</summary>
    public const int MaxWords = 4;

    /// <summary>A control for <paramref name="field"/>, whose target reads as <paramref name="label"/>.</summary>
    public RefPicker(FieldSchema field, EntitySchema? target, RowLabel? label)
    {
        ArgumentNullException.ThrowIfNull(field);

        Field = field;
        Target = target;
        Label = label;
    }

    /// <summary>The reference field.</summary>
    public FieldSchema Field { get; }

    /// <summary>The entity it points at, when the schema declares it.</summary>
    public EntitySchema? Target { get; }

    /// <summary>The target's label, or nothing when it has none and only a pasted id works.</summary>
    public RowLabel? Label { get; }

    /// <summary>Whether the chosen row can be read out by its label.</summary>
    public bool Labelled => Target is not null && Label is not null;

    /// <summary>Whether the control can search at all: a label with a field no mask may withhold.</summary>
    public bool Searchable => Labelled && Label!.Searchable.Count > 0;

    /// <summary>What the box shows: the chosen row's label, or what is being typed.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>The label of the row the reference currently names.</summary>
    public string? Chosen { get; set; }

    /// <summary>The rows the last search found.</summary>
    public IReadOnlyList<RefOption> Options { get; private set; } = [];

    /// <summary>Whether the list is open.</summary>
    public bool Open { get; set; }

    /// <summary>The option the arrow keys are on; -1 for none.</summary>
    public int Active { get; private set; } = -1;

    /// <summary>Numbers searches, so a slower earlier one cannot land after a faster later one.</summary>
    public int Version { get; set; }

    /// <summary>The option the arrow keys are on, when there is one.</summary>
    public RefOption? ActiveOption => Active >= 0 && Active < Options.Count ? Options[Active] : null;

    /// <summary>Shows a search's rows, with none of them active.</summary>
    public void Show(IReadOnlyList<RefOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Active = -1;
        Open = true;
    }

    /// <summary>Moves the active option by <paramref name="step"/>, wrapping at either end.</summary>
    public void Move(int step)
    {
        if (Options.Count == 0)
        {
            Active = -1;
            return;
        }

        Active = Active < 0 && step < 0
            ? Options.Count - 1
            : (((Active + step) % Options.Count) + Options.Count) % Options.Count;
    }

    /// <summary>Closes the list and puts the chosen row's label back in the box.</summary>
    public void Settle(Guid? id)
    {
        Open = false;
        Active = -1;
        Term = Chosen ?? id?.ToString() ?? string.Empty;
    }

    /// <summary>The read one search makes: the label's searchable fields matched per word, limited to <see cref="Limit"/>.</summary>
    /// <remarks>
    /// Sorted by the label's first searchable field only when that field is required: the data port refuses a
    /// sort on a nullable field under keyset paging, and an unsorted list is better than a refused one.
    /// </remarks>
    public AlvoQuery Query(string? term)
    {
        if (Target is not { } target || Label is not { Searchable.Count: > 0 } label)
        {
            throw new InvalidOperationException($"{Field.Name} has no labelled target this caller can search.");
        }

        var words = (term ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(MaxWords);
        List<AlvoFilter> filters = [.. words.Select(word => GridQuery.Search(label.Searchable, word)).OfType<AlvoFilter>()];
        var first = target.Fields.FirstOrDefault(field => string.Equals(field.Name, label.Searchable[0], StringComparison.Ordinal));

        return new AlvoQuery
        {
            Entity = target.Name,
            Filter = filters.Count switch { 0 => null, 1 => filters[0], _ => new AlvoAnd(filters) },
            Select = [AlvoManagedColumns.Id, .. label.Fields],
            Sort = first is { Required: true } ? [new AlvoSort(first.Name, Descending: false)] : [],
            Limit = Limit,
        };
    }

    /// <summary>A search's rows as options, each read as its label, else its short id.</summary>
    public IReadOnlyList<RefOption> OptionsOf(AlvoPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return [.. page.Items
            .Select(row => (Id: RefLabels.IdOf(row[AlvoManagedColumns.Id]), Row: row))
            .Where(pair => pair.Id.HasValue)
            .Select(pair => new RefOption(pair.Id!.Value, Label?.Of(pair.Row) ?? RefLabels.ShortId(pair.Id.Value)))];
    }

    /// <summary>A full uuid in the box, which sets the reference whatever the search found.</summary>
    public static Guid? Pasted(string? term) => RefLabels.IdOf(term?.Trim());
}
