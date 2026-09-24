using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The sheet that adds a field to an entity, or edits one it declares.</summary>
public partial class FieldEditor
{
    private const int NoteValueLimit = 40;

    private static readonly FieldType[] _types = Enum.GetValues<FieldType>();
    private static readonly string[] _flags = [string.Empty, "false", "true"];
    private static readonly FieldKind[] _kinds = Enum.GetValues<FieldKind>();

    private FieldFacets _facets = new();
    private ElementReference _nameInput;
    private string? _refusal;
    private string? _prefilled;

    /// <summary>The entity the field is added to.</summary>
    [Parameter, EditorRequired]
    public string Entity { get; set; } = string.Empty;

    /// <summary>The entities a ref may point at.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<string> Targets { get; set; } = [];

    /// <summary>
    /// The field names this entity already declares, so a rename onto one of them is refused here.
    /// </summary>
    /// <remarks>
    /// Refused in the editor rather than after it closes, for the reason every other refusal on this panel
    /// is: the operator is looking at the name they typed. <c>WorkingCopy.RenameField</c> refuses it too —
    /// it has to, being the one that writes — and this is the copy that can point at the box.
    /// </remarks>
    [Parameter]
    public IReadOnlyList<string> Siblings { get; set; } = [];

    /// <summary>Raised with the field to add, once it is one the schema can carry.</summary>
    [Parameter]
    public EventCallback<NewField> OnAdd { get; set; }

    /// <summary>
    /// The declared field being edited, or <see langword="null"/> when the panel adds a new one.
    /// </summary>
    /// <remarks>
    /// One control for both, because an edit is the same document change as an addition: the facets
    /// are written under the field's own key, and a key that already exists is replaced. A second
    /// component would be a second set of refusals to keep in step with the frozen schema.
    /// </remarks>
    [Parameter]
    public string? Editing { get; set; }

    /// <summary>The edited field's current facets, as they stand in the working copy.</summary>
    [Parameter]
    public string? EditingJson { get; set; }

    /// <summary>Raised when an edit is abandoned.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>What this build refuses, as <c>ManagementCapabilities.Refused</c> reports it.</summary>
    [Parameter]
    public IReadOnlyList<ManagementRefusedFeature> Refused { get; set; } = [];

    /// <summary>
    /// The entities a rollup here could aggregate, cascaded by the entity screen from the working copy.
    /// </summary>
    /// <remarks>
    /// Cascaded and private rather than a parameter — the <c>StagedView</c> pattern — because a component parameter
    /// must be public and <see cref="RollupSource"/> is internal; a parameter would grow the package's surface.
    /// </remarks>
    [CascadingParameter]
    private IReadOnlyList<RollupSource>? Sources { get; set; }

    /// <summary>A field the editor built, in the schema's own shape.</summary>
    /// <param name="Name">The field's name.</param>
    /// <param name="Facets">Its type and facets.</param>
    public sealed record NewField(string Name, JsonObject Facets)
    {
        /// <summary>Whether the operator asked for another field after this one, so the sheet stays open.</summary>
        /// <remarks>Internal: it passes between two screens of this assembly and is nobody else's to read.</remarks>
        internal bool KeepOpen { get; init; }
    }

    /// <summary>
    /// The refusals that belong to a field, which are the ones a reader of this panel is missing a
    /// control for.
    /// </summary>
    private IReadOnlyList<ManagementRefusedFeature> FieldRefusals =>
        [.. Refused.Where(refusal => refusal.Slot.StartsWith("field.", StringComparison.Ordinal))];

    private bool IsEditing => Editing is { Length: > 0 };

    /// <summary>The build's refusal of a rollup filter, shown where the filter would be.</summary>
    private ManagementRefusedFeature? WhereRefusal => Refused.FirstOrDefault(refusal => refusal.Slot == "rollup.where");

    /// <summary>The sources a rollup may be chosen from: the ones the apply accepts.</summary>
    private IReadOnlyList<string> RollupFroms => [.. _facets.Sources.Where(source => source.Refusal is null).Select(source => source.Entity)];

    /// <summary>The child fields the chosen op can aggregate, by name.</summary>
    private IReadOnlyList<string> AggregatableNames => [.. _facets.Aggregatable.Select(child => child.Name)];

    /// <summary>What a kind is called on its chip.</summary>
    private static string KindWord(FieldKind kind) => kind switch
    {
        FieldKind.Rollup => "rollup",
        FieldKind.Computed => "computed",
        _ => "written by callers",
    };

    /// <summary>What the chosen kind means, under its chips.</summary>
    private string KindHint => _facets.Kind switch
    {
        FieldKind.Rollup => "Maintained by Alvo: aggregated from the rows of an entity that points here, in the same transaction as the write to them. Callers never write it.",
        FieldKind.Computed => "Maintained by the database: a stored generated column computed from this row's own fields. Callers never write it.",
        _ => "A caller writes it, within the type and constraints below.",
    };

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        /* Prefilled only when the target changes: re-reading the parameters on every render would overwrite
           what the operator is in the middle of typing with what the document still says. */
        if (IsEditing && _prefilled != Editing)
        {
            _prefilled = Editing;
            _refusal = null;
            _facets = FieldFacets.Prefill(Editing!, EditingJson);
        }
        else if (!IsEditing)
        {
            _prefilled = null;
        }

        _facets.Sources = Sources ?? [];
    }

    /// <summary>Switches the kind; a computed column keeps a type it can be, and decimal otherwise.</summary>
    private void ChooseKind(FieldKind kind)
    {
        _facets.Kind = kind;
        if (kind == FieldKind.Computed && !_facets.ComputedTypesOffered.Contains(_facets.Type))
        {
            _facets.Type = FieldType.Decimal;
        }

        _refusal = null;
    }

    /// <summary>A type as the descriptor spells it.</summary>
    private static string Word(FieldType type) => type.ToString().ToLowerInvariant();

    private static int Number(object? value, int fallback)
        => int.TryParse(value?.ToString(), CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    /// <summary>A number box's value, or <see langword="null"/> when it was cleared — which is how "no limit" is said.</summary>
    private static int? OptionalNumber(object? value)
        => int.TryParse(value?.ToString(), CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>A declared value short enough to sit in a line of prose; a description can be a paragraph.</summary>
    private static string Short(string json) => json.Length <= NoteValueLimit ? json : json[..NoteValueLimit] + "…";

    private Task Cancel() => OnClose.InvokeAsync();

    private async Task Add()
    {
        if (await StageAsync(keepOpen: false) && !IsEditing)
        {
            _facets.Name = string.Empty;
            _facets.Values = string.Empty;
        }
    }

    /// <summary>
    /// Stages the field and clears the form for the next one, keeping the type: a run of fields added
    /// together is most often a run of one type.
    /// </summary>
    private async Task AddAnother()
    {
        if (!await StageAsync(keepOpen: true))
        {
            return;
        }

        _facets = new FieldFacets { Type = _facets.Type, Kind = _facets.Kind, Sources = _facets.Sources };
        await _nameInput.FocusAsync();
    }

    /// <summary>
    /// Raises the field <see cref="FieldFacets.Build"/> makes and says it did, or shows why it cannot be made.
    /// </summary>
    private async Task<bool> StageAsync(bool keepOpen)
    {
        if (_facets.Build(Editing, EditingJson, Siblings, out _refusal) is not { } facets)
        {
            return false;
        }

        await OnAdd.InvokeAsync(new NewField(_facets.Name, facets) { KeepOpen = keepOpen });
        return true;
    }
}
