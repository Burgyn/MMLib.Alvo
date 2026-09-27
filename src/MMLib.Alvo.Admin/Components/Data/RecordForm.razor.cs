using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

/* The namespace Razor already gives the markup half of this class; the feature folders (F-25) rename it. */
#pragma warning disable CA1716
namespace MMLib.Alvo.Admin.Components.Data;
#pragma warning restore CA1716

/// <summary>One record, in a form generated from the field types.</summary>
public partial class RecordForm
{
    /// <summary>How long typing into a reference has to pause before it searches.</summary>
    private static readonly TimeSpan _searchDelay = TimeSpan.FromMilliseconds(250);

    private AlvoEditor? _editor;
    private readonly RefusalState<AdminProblem> _problem = new();
    private bool _saving;

    /// <summary>Whether the values arriving next are a Reload's, which gives focus back to the form.</summary>
    private bool _reloading;
    private Dictionary<string, object?>? _opened;
    private RecordDraft _draft = new([], new Dictionary<string, object?>());
    private IReadOnlyList<FieldSchema> _editable = [];
    private IReadOnlyList<FieldSchema> _calculated = [];
    private Dictionary<string, RefPicker> _pickers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MudBlazor.MudTextField<string>> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ElementReference> _references = new(StringComparer.Ordinal);

    /// <summary>What the draft could not read, one refusal per control, keyed by the control's id (spec §3.8).</summary>
    private readonly FieldRefusals _refusals = new();

    /// <summary>The entity this record belongs to.</summary>
    [Parameter, EditorRequired]
    public EntitySchema Entity { get; set; } = default!;

    /// <summary>The record's values as read, or an empty dictionary for a new record. Read, never changed.</summary>
    [Parameter, EditorRequired]
    public Dictionary<string, object?> Values { get; set; } = default!;

    /// <summary>The record's id when editing, and nothing when creating.</summary>
    [Parameter]
    public Guid? RecordId { get; set; }

    /// <summary>Raised when the operator dismisses the form, or saves it with nothing changed.</summary>
    [Parameter]
    public EventCallback OnCancel { get; set; }

    /// <summary>Raised after a successful write.</summary>
    [Parameter]
    public EventCallback OnSaved { get; set; }

    /// <summary>
    /// Raised with the record's label when the operator asks to delete it. The page confirms and deletes, after
    /// this editor has closed, so no dialog opens over it.
    /// </summary>
    [Parameter]
    public EventCallback<string> OnDeleteRequested { get; set; }

    /// <summary>What the Data screen knows about the entity beyond its shape; see <see cref="RecordFormScope"/>.</summary>
    [CascadingParameter]
    private RecordFormScope? Scope { get; set; }

    private bool Creating => RecordId is null;

    private FieldLocks Locks => Scope?.Locks ?? FieldLocks.None;

    private string Title => Creating ? "New record" : $"Edit {LabelOf(new AlvoRecord(Values))}";

    private string Verb => Creating
        ? $"POST /api/{Entity.Name}"
        : $"PATCH /api/{Entity.Name}/{RecordId}{IfMatch}";

    /// <summary>The header the Data API takes the version this save carries in, when it carries one.</summary>
    private string IfMatch => RecordVersion.LastWriteWins(Entity, Values) ? string.Empty : " with If-Match";

    /// <summary>Opens the draft on the values it was given, once per record rather than once per render.</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (ReferenceEquals(_opened, Values))
        {
            return;
        }

        _opened = Values;
        _editable = FormFields.Editable(Entity, Locks);
        _calculated = FormFields.Shown(Entity, Scope?.Masks ?? FieldMasks.None, Locks, Creating);
        _draft = new RecordDraft(_editable, Values);
        _pickers = _editable.Where(column => column.Reference is not null)
            .ToDictionary(column => column.Name, Picker, StringComparer.Ordinal);
        _refusals.ClearAll();
        _problem.Clear();

        foreach (var picker in _pickers.Values)
        {
            await ResolveChosenAsync(picker);
        }

        FocusAfterReload();
    }

    /// <summary>After a Reload, focus goes back to the form: the panel that had it is gone with the old values.</summary>
    private void FocusAfterReload()
    {
        if (_reloading)
        {
            _reloading = false;
            _editor?.FocusFirstControl();
        }
    }

    /// <summary>
    /// Reads the record again after a write lost to another writer, and opens it as it is now; what was changed here
    /// is discarded, which the refusal's fix says (docs/todo-admin.md §8d item 25).
    /// </summary>
    private Task ReloadAsync()
    {
        if (RecordId is not { } id || Scope is null)
        {
            return Task.CompletedTask;
        }

        _reloading = true;
        return Scope.Reload(id);
    }

    private RefPicker Picker(FieldSchema column)
    {
        var name = column.Reference!.TargetEntity;
        var target = Scope?.Schema.Entities.FirstOrDefault(
            entity => string.Equals(entity.Name, name, StringComparison.Ordinal));
        var label = Scope?.Targets.TryGetValue(name, out var found) == true ? found : null;
        return new RefPicker(column, target, label) { Term = _draft.Text(column.Name) };
    }

    /// <summary>What a record is called in a title or a report: its label, else its short id.</summary>
    private string LabelOf(AlvoRecord record)
        => Scope?.Label?.Of(record)
            ?? (RefLabels.IdOf(record[AlvoManagedColumns.Id]) is { } id ? RefLabels.ShortId(id) : Entity.Name);

    private static string ControlId(FieldSchema column) => ControlId(column.Name);

    private static string ControlId(string name) => $"rf-{name}";

    private static string HintId(FieldSchema column) => $"rf-{column.Name}-hint";

    private static string LabelId(FieldSchema column) => $"rf-{column.Name}-label";

    /// <summary>What the control is described by: its hint, and the sentence under it while it is refused (spec §3.8).</summary>
    private string DescribedBy(FieldSchema column) => _refusals.DescribedBy(ControlId(column), HintId(column));

    /// <summary>Whether the control is refused, which draws it in the error tone and marks it <c>aria-invalid</c>.</summary>
    private bool Refused(FieldSchema column) => _refusals.Has(ControlId(column));

    /// <summary>
    /// Focuses a refused control. Only text controls and references can be refused, because only typed text can fail
    /// to read; a switch or a choice has nothing to focus here.
    /// </summary>
    private ValueTask FocusAsync(FieldSchema column)
    {
        if (_texts.TryGetValue(column.Name, out var text))
        {
            return text.FocusAsync();
        }

        return _references.TryGetValue(column.Name, out var reference) ? reference.FocusAsync() : ValueTask.CompletedTask;
    }

    private static string TypeOf(FieldSchema column) => column.Reference is { } reference
        ? $"ref to {reference.TargetEntity}"
        : column.Type.ToString().ToLowerInvariant();

    private void Set(string name, string? text)
    {
        _draft.Set(name, text);
        _refusals.Clear(ControlId(name));
    }

    private void Toggle(string name)
        => Set(name, string.Equals(_draft.Text(name), "true", StringComparison.Ordinal) ? "false" : "true");

    private static string OptionId(FieldSchema column, RefOption option) => $"{ControlId(column)}-{option.Id:N}";

    private static string Placeholder(RefPicker picker) => picker.Searchable
        ? $"Search {picker.Target!.Name}, or paste an id"
        : "Paste the full id";

    /// <summary>Puts the label of the row the reference names in the box; the full id when it has none.</summary>
    private async Task ResolveChosenAsync(RefPicker picker)
    {
        var id = RefLabels.IdOf(_draft.Text(picker.Field.Name));
        picker.Chosen = null;
        if (picker.Labelled && id is { } known)
        {
            try
            {
                var context = await Data.ContextAsync(CancellationToken.None);
                var labels = await Data.LabelsAsync(
                    picker.Target!.Name, picker.Label!, [known], context, CancellationToken.None);
                picker.Chosen = labels.TryGetValue(known, out var label) ? label : null;
            }
            catch (Exception exception)
            {
                /* A label masked from this caller, a target they hold no tenant for, or a read that
                   failed outright: the id is still the value, so it is what the box shows. Only the
                   last is logged — the classifier tells a refusal from a fault. */
                AdminProblem.Absorb(exception, Logger, ProblemSite.Records);
            }
        }

        picker.Settle(id);
    }

    /// <summary>A full uuid sets the reference at once; anything else searches once typing pauses.</summary>
    private async Task RefTypedAsync(RefPicker picker, string? text)
    {
        picker.Term = text ?? string.Empty;
        if (RefPicker.Pasted(picker.Term) is { } id)
        {
            picker.Version++;
            Set(picker.Field.Name, id.ToString());
            await ResolveChosenAsync(picker);
            return;
        }

        if (!picker.Searchable)
        {
            return;
        }

        var version = ++picker.Version;
        await Task.Delay(_searchDelay);
        if (version == picker.Version)
        {
            await SearchAsync(picker, picker.Term, version);
        }
    }

    /// <summary>Opens the list on the target's first rows, the way a select opens on its options.</summary>
    private async Task RefFocusedAsync(RefPicker picker)
    {
        if (picker.Searchable && !picker.Open)
        {
            await SearchAsync(picker, null, ++picker.Version);
        }
    }

    /// <summary>Leaving the box keeps the chosen row; leaving it empty clears the reference.</summary>
    private void RefLeft(RefPicker picker)
    {
        picker.Version++;
        if (string.IsNullOrWhiteSpace(picker.Term))
        {
            Set(picker.Field.Name, null);
            picker.Chosen = null;
        }

        picker.Settle(RefLabels.IdOf(_draft.Text(picker.Field.Name)));
    }

    private async Task RefKeyAsync(RefPicker picker, KeyboardEventArgs args)
    {
        switch (args.Key)
        {
            case "ArrowDown" when !picker.Open:
                await RefFocusedAsync(picker);
                break;
            case "ArrowDown":
                picker.Move(1);
                break;
            case "ArrowUp":
                picker.Move(-1);
                break;
            case "Enter" when picker.ActiveOption is { } option:
                Choose(picker, option);
                break;
            case "Escape" when picker.Open:
                picker.Version++;
                picker.Settle(RefLabels.IdOf(_draft.Text(picker.Field.Name)));
                break;
        }
    }

    private void Choose(RefPicker picker, RefOption option)
    {
        picker.Version++;
        Set(picker.Field.Name, option.Id.ToString());
        picker.Chosen = option.Label;
        picker.Settle(option.Id);
    }

    /// <summary>
    /// One search of the target, as the operator.
    /// </summary>
    /// <remarks>
    /// A refusal — a scoped target and no tenant, a query the port will not run — or a read that failed
    /// outright is an empty list rather than a red panel, the way the grid never turns red over a column it
    /// could draw anyway: pasting the id still works, and the list says so. A search is a convenience; the
    /// circuit must not end over one. A label field a CEL mask may withhold is not searched at all
    /// (<see cref="RowLabel.Searchable"/>), so that refusal no longer reaches here.
    /// </remarks>
    private async Task SearchAsync(RefPicker picker, string? term, int version)
    {
        try
        {
            var context = await Data.ContextAsync(CancellationToken.None);
            var page = await Data.PageAsync(picker.Query(term), context, CancellationToken.None);
            if (version == picker.Version)
            {
                picker.Show(picker.OptionsOf(page));
            }
        }
        catch (Exception exception)
        {
            AdminProblem.Absorb(exception, Logger, ProblemSite.Records);
            if (version == picker.Version)
            {
                picker.Show([]);
            }
        }
    }

    private static string? Required(FieldSchema column) => column.Required ? "true" : null;

    /// <summary>
    /// The class that draws a Mud input's required mark. The mark is visual only, the way the enum and reference
    /// labels hide theirs: the name stays the header, and <c>aria-required</c> says it. Not the input's own
    /// <c>Required</c>, which sets the browser's <c>required</c>: that blocks the submit before the engine is asked,
    /// and a field never read back is legitimately empty on an edit (the class remarks: no second validator).
    /// </summary>
    private static string? RequiredMark(FieldSchema column) => column.Required ? "a-required" : null;

    /// <summary>
    /// The control's type. A number is text, not <c>number</c>, and <c>inputmode</c> keeps the numeric keypad. A
    /// decimal, because a number input displays its value in the browser's locale, which is what drew <c>21,6</c>
    /// (D-8). An integer, because a number input reports text it cannot parse (<c>1e</c>, <c>--3</c>) as an empty
    /// value: the form would send an optional one as nothing, where reading the text itself refuses it at the field.
    /// </summary>
    private static string InputType(FieldSchema column) => column.Type switch
    {
        FieldType.Date => "date",
        FieldType.DateTime => "datetime-local",
        _ => "text",
    };

    /// <summary>The Mud input's type for <see cref="InputType"/>, which stays the one mapping of a field to a control.</summary>
    private static MudBlazor.InputType MudInputType(FieldSchema column) => InputType(column) switch
    {
        "date" => MudBlazor.InputType.Date,
        "datetime-local" => MudBlazor.InputType.DateTimeLocal,
        _ => MudBlazor.InputType.Text,
    };

    /// <summary>The numeric keypad for a number, which is a text input for <see cref="InputType"/>'s reason.</summary>
    private static MudBlazor.InputMode MudInputMode(FieldSchema column) => column.Type switch
    {
        FieldType.Decimal => MudBlazor.InputMode.@decimal,
        FieldType.Integer => MudBlazor.InputMode.numeric,
        _ => MudBlazor.InputMode.text,
    };

    /// <summary>
    /// Whether the field is named by a label above its control, as every field is (spec §3.8). A boolean is not: its
    /// switch is named by the text beside it, the way a checkbox is.
    /// </summary>
    private static bool OwnLabel(FieldSchema column) => column.Type != FieldType.Boolean;

    /// <summary>What the schema says about this field, in the words an author would recognise.</summary>
    /// <remarks>A text box is multi-line, so its hint also says what Ctrl/Cmd+Enter and Enter do (spec §3.4).</remarks>
    private string Hint(FieldSchema column)
        => string.Concat(DeclaredFacets(column).Concat(CallerFacets(column)).Select(part => $" · {part}"))
            + (column.Type == FieldType.Text ? $" · {ChordHint.Of("saves")}" : string.Empty);

    /// <summary>What the field's declaration admits, whoever is writing.</summary>
    private static IEnumerable<string> DeclaredFacets(FieldSchema column)
    {
        /* The format by name; its pattern is the hint's title (RecordForm.razor). A regular expression is
           written for the validator, and printed inline it was the longest and least readable part of the line. */
        if (column.Format is { Length: > 0 } format)
        {
            yield return $"must match the {format} format";
        }

        if (column.MaxLength is { } max)
        {
            yield return $"at most {max} characters";
        }

        if (column.Precision is { } precision && column.Scale is { } scale)
        {
            yield return $"{precision} digits in total, {scale} after the point";
        }

        if (column.Type == FieldType.DateTime)
        {
            yield return "in UTC";
        }

        if (column.Unique)
        {
            yield return "unique across the entity";
        }
    }

    /// <summary>What differs for some callers, or for this record: a lock, or a value never read back.</summary>
    private IEnumerable<string> CallerFacets(FieldSchema column)
    {
        if (Locks.LockedForSome(column.Name))
        {
            yield return "read-only for some callers";
        }

        if (!Creating && Scope?.Masks.NeverReturned(column.Name) == true)
        {
            yield return "never read back — type a value to replace it";
        }
    }

    /// <summary>Why a field is in the Calculated group, for the title on its name.</summary>
    private static string Why(FieldSchema column) => column switch
    {
        { ComputedExpression: { } expression } => $"computed: {expression}",
        { Rollup: { } rollup } => $"rollup: {rollup.Op.ToString().ToLowerInvariant()} over {rollup.From}",
        _ => "read-only",
    };

    private Task Cancel() => OnCancel.InvokeAsync();

    /// <summary>Writes what changed; with nothing changed, closes without a write at all.</summary>
    private async Task Save()
    {
        _problem.Clear();
        if (!Creating && !_draft.Dirty)
        {
            await OnCancel.InvokeAsync();
            return;
        }

        var changes = _draft.Read();
        _refusals.ClearAll();
        _refusals.RefuseAll(_editable.Where(column => changes.Problems.ContainsKey(column.Name))
            .Select(column => KeyValuePair.Create(ControlId(column), changes.Problems[column.Name])));
        if (!_refusals.Any)
        {
            await SubmitAsync(changes.Values);
        }
    }

    /// <summary>Sends the values the draft could read, and says what the write did.</summary>
    private async Task SubmitAsync(Dictionary<string, object?> values)
    {
        _saving = true;
        try
        {
            var saved = await WriteAsync(values);
            Scope?.Report($"{(Creating ? "Created" : "Saved")} {LabelOf(saved)}");
            if (Creating && RefLabels.IdOf(saved[AlvoManagedColumns.Id]) is { } created)
            {
                Scope?.Created(created);
            }

            await OnSaved.InvokeAsync();
        }
        catch (Exception exception)
        {
            Refused(exception);
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task<AlvoRecord> WriteAsync(Dictionary<string, object?> payload)
    {
        if (RecordId is { } id)
        {
            return await Data.UpdateAsync(
                Entity.Name, id, payload, RecordVersion.Of(Entity, Values), CancellationToken.None);
        }

        /* A create into a tenant-scoped entity carries tenant_id, and it is the one managed
           column a caller may write — `AlvoManagedColumns.IsCallerWritable` says so, because a
           create legitimately places a row in a tenant and the synthesized tenant scope
           evaluated over the candidate row is what decides whether that tenant is allowed.
           Sending the operator's own is therefore not a shortcut past the guard; it is the
           value the guard is there to check. Omitting it is what fails. */
        if (Entity.Tenancy == TenancyMode.Scoped
            && (await Data.ContextAsync(CancellationToken.None)).Tenant is { } tenant)
        {
            payload[AlvoManagedColumns.TenantId] = tenant.Value;
        }

        return await Data.CreateAsync(Entity.Name, payload, CancellationToken.None);
    }

    /// <summary>
    /// Asks the page to delete this record, once any unsaved change has been answered for: the editor closes before
    /// the confirm opens, and closing it without asking would lose the edit silently.
    /// </summary>
    private Task RequestDeleteAsync()
        => _editor?.LeaveAsync(() => OnDeleteRequested.InvokeAsync(LabelOf(new AlvoRecord(Values))))
            ?? Task.CompletedTask;

    /// <summary>Renders the refusal where it happened, with the fix a write refusal calls for.</summary>
    /// <remarks>Counted, so a second refusal draws a new panel, which takes focus and is announced again.</remarks>
    private void Refused(Exception exception)
    {
        var problem = AdminProblem.From(exception, Logger, ProblemSite.RecordWrite);
        _problem.Show(problem, title: RecordVersion.IsConflict(problem) ? RecordVersion.ConflictTitle : null);
    }
}
