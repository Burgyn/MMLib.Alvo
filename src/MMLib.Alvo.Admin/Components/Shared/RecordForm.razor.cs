using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

/* The namespace Razor already gives the markup half of this class; the feature folders (F-25) rename it. */
#pragma warning disable CA1716
namespace MMLib.Alvo.Admin.Components.Shared;
#pragma warning restore CA1716

/// <summary>One record, in a form generated from the field types.</summary>
public partial class RecordForm
{
    /// <summary>How long typing into a reference has to pause before it searches.</summary>
    private static readonly TimeSpan _searchDelay = TimeSpan.FromMilliseconds(250);

    private AdminProblem? _problem;
    private bool _saving;
    private Dictionary<string, object?>? _opened;
    private RecordDraft _draft = new([], new Dictionary<string, object?>());
    private IReadOnlyList<FieldSchema> _editable = [];
    private IReadOnlyList<FieldSchema> _calculated = [];
    private Dictionary<string, RefPicker> _pickers = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _problems = new Dictionary<string, string>(StringComparer.Ordinal);

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

    /// <summary>Raised after a successful delete.</summary>
    [Parameter]
    public EventCallback OnDeleted { get; set; }

    /// <summary>What the Data screen knows about the entity beyond its shape; see <see cref="RecordFormScope"/>.</summary>
    [CascadingParameter]
    private RecordFormScope? Scope { get; set; }

    private bool Creating => RecordId is null;

    private FieldLocks Locks => Scope?.Locks ?? FieldLocks.None;

    private string Title => Creating ? "New record" : $"Edit {LabelOf(new AlvoRecord(Values))}";

    private string Verb => Creating
        ? $"POST /api/{Entity.Name}"
        : $"PATCH /api/{Entity.Name}/{RecordId}";

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
        _problems = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var picker in _pickers.Values)
        {
            await ResolveChosenAsync(picker);
        }
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

    private static string ControlId(FieldSchema column) => $"rf-{column.Name}";

    private static string HintId(FieldSchema column) => $"rf-{column.Name}-hint";

    private static string LabelId(FieldSchema column) => $"rf-{column.Name}-label";

    private static string TypeOf(FieldSchema column) => column.Reference is { } reference
        ? $"ref to {reference.TargetEntity}"
        : column.Type.ToString().ToLowerInvariant();

    private void Set(string name, string? text)
    {
        _draft.Set(name, text);
        if (_problems.ContainsKey(name))
        {
            _problems = _problems.Where(pair => pair.Key != name)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
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
    /// The control's type. A decimal is text, not <c>number</c>: a number input displays its value in the
    /// browser's locale, which is what drew <c>21,6</c> (D-8); <c>inputmode</c> keeps the numeric keypad.
    /// </summary>
    private static string InputType(FieldSchema column) => column.Type switch
    {
        FieldType.Integer => "number",
        FieldType.Date => "date",
        FieldType.DateTime => "datetime-local",
        _ => "text",
    };

    private static string? InputMode(FieldSchema column) => column.Type == FieldType.Decimal ? "decimal" : null;

    /// <summary>What the schema says about this field, in the words an author would recognise.</summary>
    private string Hint(FieldSchema column)
        => string.Concat(DeclaredFacets(column).Concat(CallerFacets(column)).Select(part => $" · {part}"));

    /// <summary>What the field's declaration admits, whoever is writing.</summary>
    private static IEnumerable<string> DeclaredFacets(FieldSchema column)
    {
        if (column.Format is { Length: > 0 } format)
        {
            yield return column.FormatPattern is { Length: > 0 } pattern
                ? $"must match {format} — {pattern}"
                : $"must match {format}";
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
        _problem = null;
        if (!Creating && !_draft.Dirty)
        {
            await OnCancel.InvokeAsync();
            return;
        }

        var changes = _draft.Read();
        _problems = changes.Problems;
        if (_problems.Count == 0)
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
            return await Data.UpdateAsync(Entity.Name, id, payload, CancellationToken.None);
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

    private async Task Delete()
    {
        if (RecordId is not { } id)
        {
            return;
        }

        _saving = true;
        try
        {
            await Data.DeleteAsync(Entity.Name, id, CancellationToken.None);
            Scope?.Report($"Deleted {LabelOf(new AlvoRecord(Values))}");
            await OnDeleted.InvokeAsync();
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

    /// <summary>Renders the refusal where it happened, with the fix a write refusal calls for.</summary>
    private void Refused(Exception exception)
        => _problem = AdminProblem.From(exception, Logger, ProblemSite.RecordWrite);
}
