using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The editor that adds a field to an entity, or edits one it declares.</summary>
public partial class FieldEditor
{
    private const string ComputedBox = "new-field-computed";

    private const int NoteValueLimit = 40;

    /// <summary>The build's slot for a rollup's refused <c>where</c> filter (<c>UnhonouredFeatures.RollupWhere</c>).</summary>
    private const string RollupWhereSlot = "rollup.where";

    private static readonly FieldType[] _types = Enum.GetValues<FieldType>();
    private static readonly string[] _flags = [string.Empty, "false", "true"];
    private static readonly FieldKind[] _kinds = Enum.GetValues<FieldKind>();

    private FieldFacets _facets = new();
    private MudBlazor.MudTextField<string>? _nameField;
    private readonly RefusalState<string> _refusal = new();
    private string? _prefilled;
    private string _opened = string.Empty;
    private readonly SubmitGate _another = new();

    private const string MaxBox = "new-field-max";
    private const string PrecisionBox = "new-field-precision";
    private const string ScaleBox = "new-field-scale";

    /// <summary>What each number box holds as typed, once typed into; until then it shows the facet.</summary>
    private readonly Dictionary<string, string?> _numberTexts = new(StringComparer.Ordinal);

    /// <summary>The number boxes the editor could not read, refused at the box (spec §3.8).</summary>
    private readonly MMLib.Alvo.Admin.Components.DesignSystem.FieldRefusals _numbers = new();
    private MudBlazor.MudTextField<string>? _maxField;
    private MudBlazor.MudTextField<string>? _precisionField;
    private MudBlazor.MudTextField<string>? _scaleField;

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
    /// The refusals that belong to a field — its facets and its rollup — by <see cref="RefusalPlaces"/>, less the one
    /// already drawn at its control.
    /// </summary>
    /// <remarks>
    /// <b>Drawn once.</b> While the rollup section is open, <c>rollup.where</c> is said beside the filter it refuses
    /// (<c>rollup-where-refused</c>) — spec §3.8 puts a field's refusal at its control — so the fold leaves it out
    /// rather than repeat the same sentence in one dialog. For any other kind the fold is the only place it is said.
    /// </remarks>
    private IReadOnlyList<ManagementRefusedFeature> FieldRefusals =>
        [.. RefusalPlaces.On(RefusalScreen.FieldEditor, Refused)
            .Where(refusal => !(_facets.Kind == FieldKind.Rollup && refusal.Slot == RollupWhereSlot))];

    /// <summary>
    /// The working copy a typed expression is checked against, on a clone — cascaded by the entity screen only, so its
    /// model stays internal.
    /// </summary>
    [CascadingParameter]
    private WorkingCopy? Copy { get; set; }

    private readonly ExpressionCheck _check = new();
    private readonly ComponentLifetime _lifetime = new();

    /// <summary>Redraws the box whenever a check has something new to show.</summary>
    public FieldEditor() => _check.Changed += Redraw;

    private void Redraw() => _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose()
    {
        _check.Changed -= Redraw;
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The expression box is described by its hint, and by the sentence under it while it has one.</summary>
    private string ComputedDescribedBy
        => _check.DescribedBy(ComputedBox) is { } check ? $"{ComputedBox}-hint {check}" : $"{ComputedBox}-hint";

    private void TypeComputed(string? text)
    {
        _facets.Computed = text ?? string.Empty;
        _ = CheckComputedAsync();
    }

    /// <summary>The name is the slot's address, so a name typed after the expression asks again.</summary>
    private void TypeName(string? name)
    {
        _facets.Name = name ?? string.Empty;
        _ = CheckComputedAsync();
    }

    private void ChooseType(FieldType type)
    {
        _facets.Type = type;
        _ = CheckComputedAsync();
    }

    /// <summary>
    /// Runs the check to its end and observes its fault: it is fire-and-forget, so an unobserved exception would
    /// otherwise vanish, and a helper that fails must never be the reason the form misbehaves.
    /// </summary>
    private async Task CheckComputedAsync()
    {
        try
        {
            var text = _facets.Kind == FieldKind.Computed ? _facets.Computed : string.Empty;
            await _check.SubmitAsync(ComputedBox, text, AskAsync);
        }
        catch (Exception ex)
        {
            CheckFailed(Logger, ex);
        }
    }

    /// <summary>Fixed text and the exception only: the entity name is the caller's string and is never logged raw.</summary>
    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "The expression check on the new computed field failed")]
    private static partial void CheckFailed(ILogger logger, Exception exception);

    /// <summary>
    /// The field exactly as <see cref="FieldFacets.Build"/> would stage it, with the typed expression, or nothing while
    /// the form cannot build one yet.
    /// </summary>
    private Task<ManagementExpressionVerdict?> AskAsync(string source, CancellationToken ct)
        => !_lifetime.Ended && Copy is { } copy && Candidate(copy, source) is { } slot
            ? Gateway.CheckExpressionAsync(slot.Json, slot.Path, source, ct)
            : Task.FromResult<ManagementExpressionVerdict?>(null);

    private (string Json, string Path)? Candidate(WorkingCopy copy, string source)
        => _facets.Build(Editing, EditingJson, Siblings, out _) is { } facets
            ? ExpressionSlots.ForComputed(copy.Json, Entity, Editing, _facets.Name, facets, source)
            : null;

    private bool IsEditing => Editing is { Length: > 0 };

    /// <summary>Whether the form differs from how it opened, which is what Escape must not throw away.</summary>
    private bool Dirty => Fingerprint() != _opened;

    /// <summary>The build's refusal of a rollup filter, shown where the filter would be.</summary>
    private ManagementRefusedFeature? WhereRefusal => Refused.FirstOrDefault(refusal => refusal.Slot == RollupWhereSlot);

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
        var prefilledNow = false;
        if (IsEditing && _prefilled != Editing)
        {
            _prefilled = Editing;
            _refusal.Clear();
            _facets = FieldFacets.Prefill(Editing!, EditingJson);
            ForgetNumbers();
            prefilledNow = true;
        }
        else if (!IsEditing)
        {
            _prefilled = null;
        }

        _facets.Sources = Sources ?? [];
        if (_opened.Length == 0 || prefilledNow)
        {
            _opened = Fingerprint();
        }
    }

    /// <summary>
    /// The field the form would stage, or, when it could not stage one yet, the choices that are made so far. Two
    /// forms with the same fingerprint lose nothing by closing.
    /// </summary>
    /// <remarks>
    /// The name leads, because the facets <see cref="FieldFacets.Build"/> returns are the ones written under the
    /// name, and a rename changes the name alone.
    /// </remarks>
    private string Fingerprint()
        => string.Join('|', _facets.Name, _facets.Build(Editing, EditingJson, Siblings, out _)?.ToJsonString()
            ?? string.Join('|', "unbuilt", _facets.Kind, _facets.Type, _facets.Values, _facets.Computed));

    /// <summary>Switches the kind; a computed column keeps a type it can be, and decimal otherwise.</summary>
    private void ChooseKind(FieldKind kind)
    {
        _facets.Kind = kind;
        _ = CheckComputedAsync();
        if (kind == FieldKind.Computed && !_facets.ComputedTypesOffered.Contains(_facets.Type))
        {
            _facets.Type = FieldType.Decimal;
        }

        _refusal.Clear();
    }

    /// <summary>A type as the descriptor spells it.</summary>
    private static string Word(FieldType type) => type.ToString().ToLowerInvariant();

    /// <summary>What a number box shows: the text typed into it, or the facet it opened on.</summary>
    private string? NumberText(string box, int? facet)
        => _numberTexts.TryGetValue(box, out var typed) ? typed : facet?.ToString(CultureInfo.InvariantCulture);

    private void TypeMaxLength(string? text) => _facets.MaxLength = Typed(MaxBox, text, FacetNumber.MaxLength(text), null);

    private void TypePrecision(string? text) => _facets.Precision = Typed(PrecisionBox, text, FacetNumber.Precision(text), 10)!.Value;

    private void TypeScale(string? text) => _facets.Scale = Typed(ScaleBox, text, FacetNumber.Scale(text), 2)!.Value;

    /// <summary>
    /// Keeps what was typed and forgets the box's refusal; answers the facet it reads as, or <paramref name="fallback"/>
    /// while it cannot be read. The fallback only keeps the form's fingerprint whole: an unreadable box is refused
    /// before anything is staged (<see cref="NumbersRead"/>).
    /// </summary>
    private int? Typed(string box, string? text, FacetRead read, int? fallback)
    {
        _numberTexts[box] = text;
        _numbers.Clear(box);
        return read.Problem is null ? read.Value : fallback;
    }

    /// <summary>Refuses, at the box, every number box the form shows and cannot read; true when there is none.</summary>
    private bool NumbersRead()
    {
        _numbers.ClearAll();
        if (_facets.Kind != FieldKind.Rollup)
        {
            _numbers.RefuseAll(ShownNumbers()
                .Where(box => box.Read.Problem is not null)
                .Select(box => KeyValuePair.Create(box.Id, box.Read.Problem!)));
        }

        return !_numbers.Any;
    }

    /// <summary>The number boxes the chosen type draws, in the order it draws them, each as it reads.</summary>
    private IEnumerable<(string Id, FacetRead Read)> ShownNumbers() => _facets.Type switch
    {
        FieldType.String => [(MaxBox, FacetNumber.MaxLength(NumberText(MaxBox, _facets.MaxLength)))],
        FieldType.Decimal =>
        [
            (PrecisionBox, FacetNumber.Precision(NumberText(PrecisionBox, _facets.Precision))),
            (ScaleBox, FacetNumber.Scale(NumberText(ScaleBox, _facets.Scale))),
        ],
        _ => [],
    };

    /// <summary>Forgets what was typed into the number boxes, as a fresh or prefilled form does.</summary>
    private void ForgetNumbers()
    {
        _numberTexts.Clear();
        _numbers.ClearAll();
    }

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
    /// <remarks>
    /// Behind a gate of its own, and a no-op on an untouched form: the second click of a double click lands on the form
    /// the first one cleared, and staging that would be a refusal of a field nobody asked for.
    /// </remarks>
    private async Task AddAnother()
    {
        if (!Dirty || !_another.TryBegin())
        {
            return;
        }

        try
        {
            if (await StageAsync(keepOpen: true))
            {
                _facets = new FieldFacets { Type = _facets.Type, Kind = _facets.Kind, Sources = _facets.Sources };
                _ = CheckComputedAsync();
                ForgetNumbers();
                _opened = Fingerprint();
                if (_nameField is not null)
                {
                    await _nameField.FocusAsync();
                }
            }
        }
        finally
        {
            _another.End();
        }
    }

    /// <summary>
    /// Raises the field <see cref="FieldFacets.Build"/> makes and says it did, or shows why it cannot be made.
    /// </summary>
    private async Task<bool> StageAsync(bool keepOpen)
    {
        if (!NumbersRead())
        {
            return false;
        }

        if (_facets.Build(Editing, EditingJson, Siblings, out var refusal) is not { } facets)
        {
            _refusal.Show(refusal);
            return false;
        }

        await OnAdd.InvokeAsync(new NewField(_facets.Name, facets) { KeepOpen = keepOpen });
        return true;
    }
}
