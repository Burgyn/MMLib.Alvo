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
    private static readonly FieldType[] _types = Enum.GetValues<FieldType>();
    private static readonly string[] _flags = [string.Empty, "false", "true"];

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

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!IsEditing)
        {
            _prefilled = null;
            _facets.MaintainedElsewhere = false;
            return;
        }

        /* Only when the target changes: re-reading the parameters on every render would overwrite
           what the operator is in the middle of typing with what the document still says. */
        if (_prefilled == Editing)
        {
            return;
        }

        _prefilled = Editing;
        _refusal = null;
        _facets = FieldFacets.Prefill(Editing!, EditingJson);
    }

    /// <summary>A type as the descriptor spells it.</summary>
    private static string Word(FieldType type) => type.ToString().ToLowerInvariant();

    private static int Number(object? value, int fallback)
        => int.TryParse(value?.ToString(), CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

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

        _facets = new FieldFacets { Type = _facets.Type };
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
