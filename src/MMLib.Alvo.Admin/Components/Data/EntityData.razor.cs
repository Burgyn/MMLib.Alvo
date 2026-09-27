using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>One entity's records, read as the signed-in operator, with the record form over them.</summary>
public partial class EntityData
{
    private const int PageSize = 25;

    /// <summary>How long typing has to pause before the search runs.</summary>
    private static readonly TimeSpan _searchDelay = TimeSpan.FromMilliseconds(250);

    private static readonly string[] _nextThenPrevious = ["[data-testid='grid-next']", "[data-testid='grid-previous']"];
    private static readonly string[] _previousThenNext = ["[data-testid='grid-previous']", "[data-testid='grid-next']"];
    private static readonly string[] _searchThenNew = ["[data-testid='grid-search']", "[data-testid='record-new']"];

    /// <summary>
    /// Where focus goes once the page just read is drawn: the pager button that was pressed unmounts on the first or
    /// last page, and focus would otherwise fall to the page.
    /// </summary>
    private IReadOnlyList<string>? _focusAfterRender;

    /// <summary>Counts focus moves, so each one draws its own <c>FocusFirstOnRender</c>.</summary>
    private int _focusMoves;

    private readonly List<string> _cursors = [];
    private SchemaModel? _schema;
    private EntitySchema? _entity;
    private string _descriptor = string.Empty;
    private FieldMasks _masks = FieldMasks.None;
    private IReadOnlyList<FieldSchema> _columns = [];
    private RowLabel? _label;
    private RecordFormScope? _scope;
    private PendingDelete? _deleting;
    private bool _deletingNow;

    /// <summary>The record a delete lost to another writer for, whose row Reload gives focus to.</summary>
    private Guid? _conflicted;

    /// <summary>Whether the refusal on show is a write's, the one kind the page's Reload answers.</summary>
    private bool _writeRefused;
    private Guid? _created;

    /// <summary>
    /// The record just created that the grid is narrowed to, because the page it would have been read back on was not
    /// certain to hold it (spec §3.5, amended 27 Sep; docs/todo-admin.md §8d item 44).
    /// </summary>
    private Guid? _revealing;

    /// <summary>The search and page a reveal replaced, which ending it puts back.</summary>
    private GridView? _beforeReveal;

    /// <summary>
    /// Whether a reveal ended because its record was no longer there to read — deleted by another writer, or no longer
    /// admitted — which the grid says once, rather than an empty narrowed page naming a read rule.
    /// </summary>
    private bool _revealLost;
    private RowFocus? _focusRow;
    private AlvoButton? _newRecord;
    private IReadOnlyDictionary<string, RowLabel> _targets = new Dictionary<string, RowLabel>(StringComparer.Ordinal);
    private IReadOnlyList<string> _searchable = [];
    private IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, string>> _labels
        = new Dictionary<string, IReadOnlyDictionary<Guid, string>>(StringComparer.Ordinal);
    private AlvoPage? _page;
    private AlvoContext? _context;
    private Dictionary<string, object?>? _form;
    private Guid? _editing;
    private readonly RefusalState<AdminProblem> _problem = new();
    private string? _cursor;
    private string? _opened;
    private string _search = string.Empty;
    private GridSort? _sort;
    private int _searchVersion;
    private int _loadVersion;
    private bool _unreachable;
    private string _who = string.Empty;
    private bool _loading = true;

    /// <summary>Whether a page already on screen is being read again, which the grid's pane shows (spec §3.6).</summary>
    private bool _refreshing;

    /// <summary>The entity being browsed, from the route.</summary>
    [Parameter]
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// A record to open on arrival — what a reference cell on another entity's grid links to.
    /// </summary>
    /// <remarks>
    /// In the query rather than the path, because it is a state of this screen (a sheet over the
    /// grid), not a screen of its own; the grid underneath is the same one either way, and closing the
    /// sheet removes it again so a reload does not reopen what was just closed.
    /// </remarks>
    [SupplyParameterFromQuery(Name = "record")]
    private string? Record { get; set; }

    /// <summary>
    /// Who the rows are being read as, in the words an operator recognises.
    /// </summary>
    /// <remarks>
    /// The caller's id is a uuid, and a uuid answers a question nobody asked: the operator knows who
    /// they signed in as, and what they need confirmed is that this screen is reading as <em>them</em>
    /// rather than as the framework. The id is still what the tenant guard and the rules see, so it
    /// stays beside the name rather than being replaced by it.
    /// </remarks>
    private async Task<string> WhoAsync()
    {
        if (_context?.User is not { } user || user == default)
        {
            return "an unauthenticated caller";
        }

        var named = await Gateway.AuthorAsync();

        return named is { Length: > 0 } ? named : user.ToString();
    }

    /// <summary>
    /// Whether this screen's rows are unreachable before any rule is consulted.
    /// </summary>
    /// <remarks>
    /// A scoped entity read by a caller holding no tenant is refused by the tenant guard, which runs
    /// first (§2.7). That is an expected state of this build rather than a fault, so the screen says
    /// it once, in the words the Data list already uses, instead of raising a red panel titled
    /// <em>"Something went wrong"</em> over a skeleton that will never resolve — and it does not
    /// make the request at all, because the answer is known before it is sent.
    /// </remarks>
    private bool OutOfScope
        => _entity is { Tenancy: TenancyMode.Scoped } && _context?.Tenant is null;

    /// <summary>
    /// Why a page can be empty, said rather than left to be guessed.
    /// </summary>
    /// <remarks>
    /// This is the RLS surprise, and it is the single most confusing thing about a rule-guarded
    /// API: a rule that excludes you produces <b>200 with an empty page</b> on a list, and
    /// <b>404</b> on a get — never a 403. An operator staring at an empty grid has no way to tell
    /// that from "there are no rows" unless the screen says so.
    /// </remarks>
    private string EmptyExplanation => _entity!.Tenancy == TenancyMode.Scoped && _context?.Tenant is null
        ? "This entity is tenant-scoped and you hold no tenant. An administrator grants one in Access."
        : "Either nothing has been created yet, or the read rule on this entity does not admit you — a rule that excludes you returns an empty page rather than a refusal. The Rules screen shows the predicate it applied.";

    private bool Searching => _search.Trim().Length > 0;

    /// <summary>What the grid draws of the current page; only read once there is one.</summary>
    /// <remarks>
    /// The delete confirm counts as the sheet: it replaces the editor, and focus goes back to the row when it closes.
    /// </remarks>
    private RecordGridScope GridScope
        => new(_page!, _columns, _masks, _label, _labels, _sort, HasPrevious: _cursors.Count > 0,
            SheetOpen: _form is not null || _deleting is not null, Created: _created, FocusRow: _focusRow,
            Refusals: _problem.Key);

    /// <summary>
    /// Reads the entity when the route names a new one, then opens whatever record the query names.
    /// </summary>
    /// <remarks>
    /// Only a change of entity reloads. Opening and closing a linked record changes the query string
    /// and nothing else, and a reload there would throw away the operator's search, sort and page.
    /// </remarks>
    protected override async Task OnParametersSetAsync()
    {
        if (!string.Equals(_opened, EntityName, StringComparison.Ordinal))
        {
            await OpenEntityAsync();
        }

        await OpenLinkedRecordAsync();
    }

    /// <summary>Reads the entity afresh: its shape, who is reading, and the first page when there is one.</summary>
    private async Task OpenEntityAsync()
    {
        ResetForEntity();
        try
        {
            await DescribeAsync();
            _context = await Records.ContextAsync(CancellationToken.None);
            _who = await WhoAsync();

            if (_entity is not null && !OutOfScope)
            {
                await LoadAsync();
            }
        }
        catch (Exception exception)
        {
            Refused(exception);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Forgets the previous entity's page, search, sort, sheet and status.</summary>
    private void ResetForEntity()
    {
        _loading = true;
        _problem.Clear();
        _opened = EntityName;
        _search = string.Empty;
        _sort = null;
        _revealing = null;
        _beforeReveal = null;
        _page = null;
        _created = null;
        _focusRow = null;
        CloseSheet();
        ResetPaging();
    }

    /// <summary>The entity's shape and masks, and what the grid makes of them.</summary>
    private async Task DescribeAsync()
    {
        _schema = await Gateway.SchemaAsync(CancellationToken.None);
        _descriptor = (await Gateway.DescriptorAsync(CancellationToken.None)).DescriptorJson;
        _entity = _schema.Entities.FirstOrDefault(
            entity => string.Equals(entity.Name, EntityName, StringComparison.Ordinal));
        _masks = DescriptorLens.Masks(_descriptor, EntityName);
        _columns = _entity is null ? [] : GridColumns.Choose(_entity, _masks);
        _label = _entity is null ? null : RefLabels.For(_entity, _masks);
        _searchable = _entity is null ? [] : GridQuery.Searchable(_entity, _masks);
        _targets = Targets();
        _scope = _entity is null ? null : new RecordFormScope(
            _schema, _label, _masks, DescriptorLens.Locks(_descriptor, EntityName), _targets, Report,
            CreatedRecord, ReloadRecordAsync);
    }

    /// <summary>What the record form says after a write: a snackbar, because the write worked (spec §3.3).</summary>
    private void Report(string status) => Snackbar.Confirm(status);

    /// <summary>
    /// The label of every entity a reference on this entity points at, worked out once per entity rather
    /// than once per page.
    /// </summary>
    /// <remarks>
    /// Keyed by the target alone, because the label is a function of the target: two columns pointing
    /// at the same entity share one read. A target with no label is absent, and its cells show short ids.
    /// Every reference rather than only the shown columns, because the record form searches each of them;
    /// a target no column shows costs the grid nothing, since it has no ids on the page to look up.
    /// </remarks>
    private Dictionary<string, RowLabel> Targets()
    {
        var targets = new Dictionary<string, RowLabel>(StringComparer.Ordinal);
        var references = _entity?.Fields.Select(field => field.Reference?.TargetEntity).OfType<string>() ?? [];
        foreach (var name in references.Distinct())
        {
            var target = _schema!.Entities.FirstOrDefault(
                entity => string.Equals(entity.Name, name, StringComparison.Ordinal));
            if (target is not null && RefLabels.For(target, DescriptorLens.Masks(_descriptor, name)) is { } label)
            {
                targets[name] = label;
            }
        }

        return targets;
    }

    /// <summary>
    /// Reads the current page and the labels its references point at.
    /// </summary>
    /// <remarks>
    /// Numbered, because a search fires a read per pause in typing and a slower earlier read must not
    /// land after a faster later one — the grid would show the rows for a term no longer in the box.
    /// </remarks>
    private async Task LoadAsync()
    {
        await ReadPageAsync();
        if (_revealing is not null && _page is { Items.Count: 0 })
        {
            EndReveal();
            _revealLost = true;
            await ReadPageAsync();
        }
    }

    /// <summary>One read of the page, the labels it needs, and its refusal when there is one.</summary>
    private async Task ReadPageAsync()
    {
        var version = ++_loadVersion;
        _refreshing = _page is not null;
        try
        {
            var query = GridQuery.Page(
                _entity!, GridQuery.Filter(_searchable, _search, _revealing), _sort, PageSize, _cursor);
            var context = await Records.ContextAsync(CancellationToken.None);
            var page = await Records.PageAsync(query, context, CancellationToken.None);
            var labels = await LabelsAsync(page, context);
            if (version != _loadVersion)
            {
                return;
            }

            _page = page;
            _labels = labels;
            _problem.Clear();
            _refreshing = false;
        }
        catch (Exception exception)
        {
            if (version == _loadVersion)
            {
                _refreshing = false;
                Refused(exception);
            }
            else
            {
                /* Nobody reads a superseded load's panel, but a fault in it still belongs in the log. */
                AdminProblem.Absorb(exception, Logger, Site);
            }
        }
    }

    /// <summary>The labels the page's references point at, by target entity.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, string>>> LabelsAsync(
        AlvoPage page, AlvoContext context)
    {
        var labels = new Dictionary<string, IReadOnlyDictionary<Guid, string>>(StringComparer.Ordinal);
        foreach (var (target, label) in _targets)
        {
            var values = _columns
                .Where(column => string.Equals(column.Reference?.TargetEntity, target, StringComparison.Ordinal))
                .SelectMany(column => page.Items.Select(row => row[column.Name]));
            if (await TargetLabelsAsync(target, label, values, context) is { } resolved)
            {
                labels[target] = resolved;
            }
        }

        return labels;
    }

    /// <summary>
    /// One target's labels, or nothing — and nothing is a state, not a fault.
    /// </summary>
    /// <remarks>
    /// A label field masked from this caller, or a scoped target read with no tenant, is refused by the
    /// data port. Either leaves the cell its short id, which is still a link to the record — so the grid
    /// never turns red over a column it could draw anyway.
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, string>?> TargetLabelsAsync(
        string target, RowLabel label, IEnumerable<object?> values, AlvoContext context)
    {
        try
        {
            return await Records.LabelsAsync(target, label, values, context, CancellationToken.None);
        }
        catch (AlvoAuthorizationException)
        {
            return null;
        }
    }

    /// <summary>Opens the record the query string names, once, when it names one.</summary>
    private async Task OpenLinkedRecordAsync()
    {
        _unreachable = false;
        if (RefLabels.IdOf(Record) is not { } id || _entity is null || OutOfScope
            || (_form is not null && _editing == id))
        {
            return;
        }

        try
        {
            if (await Records.GetAsync(EntityName, id, CancellationToken.None) is { } record)
            {
                Open(record);
            }
            else
            {
                _unreachable = true;
            }
        }
        catch (Exception exception)
        {
            Refused(exception);
        }
    }

    /// <summary>Turns a refusal into the panel, with the fix this entity's state calls for.</summary>
    private void Refused(Exception exception)
    {
        _writeRefused = false;
        _problem.Show(AdminProblem.From(exception, Logger, Site), fromPress: false);
    }

    /// <summary>Whether the page offers Reload: only for a write that lost to another writer.</summary>
    private bool LostWrite => _writeRefused && RecordVersion.IsConflict(_problem.Current);

    /// <summary>
    /// A scoped entity read with no tenant is refused by the tenant guard, and the fix says so rather than
    /// sending the operator to the entity's rules.
    /// </summary>
    private ProblemSite Site
        => _entity?.Tenancy == TenancyMode.Scoped && _context?.Tenant is null
            ? ProblemSite.ScopedRecordsWithoutTenant
            : ProblemSite.Records;

    /// <summary>Takes what was typed into the search, and runs it once typing pauses; typing ends a reveal at once.</summary>
    private Task SearchTyped(string? search)
    {
        _search = search ?? string.Empty;
        _revealing = null;
        _beforeReveal = null;
        _revealLost = false;
        return SearchChanged();
    }

    /// <summary>
    /// Names the record a create wrote, so the grid selects and lights it once the page is read again; when the page on
    /// screen is not the whole entity with room on it, the grid is narrowed to that record in the same one read.
    /// </summary>
    /// <param name="id">The record's id.</param>
    private void CreatedRecord(Guid id)
    {
        var whole = _page is not null
            && GridQuery.ShowsEverything(_page, Searching || _revealing is not null, _cursors.Count > 0, PageSize);
        if (!whole)
        {
            _beforeReveal ??= new GridView(_search, _cursor, [.. _cursors]);
            _searchVersion++;
            _search = string.Empty;
            ResetPaging();
            _revealing = id;
        }

        _created = id;
    }

    /// <summary>
    /// Ends a reveal: the search and page it replaced again, and focus to the search, or to New record without one.
    /// </summary>
    private async Task ClearRevealAsync()
    {
        EndReveal();
        await LoadAsync();
        _focusAfterRender = _searchThenNew;
        _focusMoves++;
    }

    /// <summary>Takes the reveal away and puts back the search and page it replaced; the record is no longer lit.</summary>
    private void EndReveal()
    {
        var before = _beforeReveal ?? new GridView(string.Empty, null, []);
        _revealing = null;
        _beforeReveal = null;
        ResetPaging();
        _search = before.Search;
        _cursor = before.Cursor;
        _cursors.AddRange(before.Cursors);
    }

    /// <summary>Runs the search once typing pauses, from the first page.</summary>
    private async Task SearchChanged()
    {
        var version = ++_searchVersion;
        await Task.Delay(_searchDelay);
        if (version != _searchVersion)
        {
            return;
        }

        ResetPaging();
        await LoadAsync();
    }

    private async Task SortBy(FieldSchema column)
    {
        _sort = GridQuery.Next(_sort, column.Name);
        ResetPaging();
        await LoadAsync();
    }

    /// <summary>Back to the first page, for a new entity, search or sort — and a pager focus move no longer applies.</summary>
    private void ResetPaging()
    {
        _created = null;
        _revealLost = false;
        _focusAfterRender = null;
        _cursors.Clear();
        _cursor = null;
    }

    /// <summary>Moves to the next page; focus stays on Next, or goes to Previous on the last page.</summary>
    private async Task Next()
    {
        if (_page?.NextCursor is { Length: > 0 } next)
        {
            _cursors.Add(_cursor ?? string.Empty);
            _cursor = next;
            await LoadAsync();
            _focusAfterRender = _nextThenPrevious;
            _focusMoves++;
        }
    }

    /// <summary>Moves back a page; focus stays on Previous, or goes to Next on the first page.</summary>
    private async Task Previous()
    {
        if (_cursors.Count > 0)
        {
            _cursor = _cursors[^1] is { Length: > 0 } previous ? previous : null;
            _cursors.RemoveAt(_cursors.Count - 1);
            await LoadAsync();
            _focusAfterRender = _previousThenNext;
            _focusMoves++;
        }
    }

    private void NewRecord()
    {
        _created = null;
        _editing = null;
        _form = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private void Open(AlvoRecord record)
    {
        _editing = RefLabels.IdOf(record[AlvoManagedColumns.Id]);
        _form = _entity!.Fields.ToDictionary(
            column => column.Name, column => record[column.Name], StringComparer.Ordinal);
    }

    /// <summary>Closes the sheet, and drops the linked record from the address so a reload does not reopen it.</summary>
    private void CloseForm()
    {
        CloseSheet();
        if (Record is not null)
        {
            Navigation.NavigateTo(AdminPaths.Records(EntityName), replace: true);
        }
    }

    private void CloseSheet()
    {
        _form = null;
        _editing = null;
    }

    private async Task Saved()
    {
        CloseForm();
        await LoadAsync();
    }

    /// <summary>
    /// Closes the editor and asks: the confirm comes after the editor, never over it (spec §3.1).
    /// </summary>
    /// <param name="label">What the confirm calls the record.</param>
    private void AskToDelete(string label)
    {
        if (_editing is { } id)
        {
            var version = RecordVersion.Of(_entity!, _form!);
            CloseForm();
            _deleting = new PendingDelete(id, label, version);
        }
    }

    /// <summary>
    /// After a save that lost to another writer: reads the page and the record again, and opens the record as it is
    /// now — or closes the editor when it is gone, which the page read again then shows (item 25).
    /// </summary>
    /// <remarks>
    /// Drawn here, because the editor calls it through its scope rather than an <c>EventCallback</c>, which would
    /// have redrawn this screen for it — and a public parameter on the editor is what the scope exists to avoid.
    /// </remarks>
    /// <param name="id">The record the editor holds.</param>
    private async Task ReloadRecordAsync(Guid id)
    {
        await LoadAsync();
        await ReopenAsync(id);
        StateHasChanged();
    }

    /// <summary>Opens the record as it is now, or closes the editor when it is gone or cannot be read.</summary>
    private async Task ReopenAsync(Guid id)
    {
        try
        {
            if (await Records.GetAsync(EntityName, id, CancellationToken.None) is { } record)
            {
                Open(record);
                return;
            }
        }
        catch (Exception exception)
        {
            Refused(exception);
        }

        CloseForm();
    }

    /// <summary>
    /// After a delete that lost to another writer: reads the page again, the refusal goes with the read, and focus
    /// goes to the record's row, or to New record when it is gone.
    /// </summary>
    private async Task ReloadPageAsync()
    {
        var conflicted = _conflicted;
        _conflicted = null;
        await LoadAsync();
        if (conflicted is { } id && _page?.Items.Any(row => RefLabels.IdOf(row[AlvoManagedColumns.Id]) == id) == true)
        {
            _focusRow = new RowFocus(id);
        }
        else
        {
            await FocusNewRecordAsync();
        }
    }

    /// <summary>
    /// Cancel and Escape keep the record, and focus goes to its row, since the Delete that asked closed with the editor
    /// (spec §3.2); a delete already under way is left to answer.
    /// </summary>
    private Task CancelDelete()
    {
        if (_deletingNow)
        {
            return Task.CompletedTask;
        }

        _deleting = null;
        return Interop.FocusFirstOnceClosedAsync(
            ["[data-testid='record-grid'] [aria-selected='true']", "[data-testid='record-new']"]);
    }

    /// <summary>
    /// Deletes what the confirm named, and puts focus where the deleted row was.
    /// </summary>
    /// <remarks>
    /// In the one order every confirm follows (spec §3.2): the confirm closes as its verb is pressed, the write runs,
    /// its result is drawn, and focus moves last. Closed first, the library's own focus return (to what had focus when
    /// the confirm opened) is spent before the result is drawn, so it cannot undo the focus the result sets, a refusal's
    /// panel included. The verb's own gate keeps a double click to one delete while the confirm is closing.
    /// </remarks>
    private async Task DeleteAsync()
    {
        if (_deleting is not { } target || _deletingNow)
        {
            return;
        }

        _deleting = null;
        _deletingNow = true;
        var deleted = await TryDeleteAsync(target);
        _deletingNow = false;
        if (deleted && _focusRow is null)
        {
            await FocusNewRecordAsync();
        }
    }

    /// <summary>Deletes, says so, reads the page again, and names the row that takes the deleted one's place.</summary>
    private async Task<bool> TryDeleteAsync(PendingDelete target)
    {
        var at = _page?.Items.ToList().FindIndex(row => RefLabels.IdOf(row[AlvoManagedColumns.Id]) == target.Id) ?? -1;
        try
        {
            await Records.DeleteAsync(EntityName, target.Id, target.Version, CancellationToken.None);
            Snackbar.Confirm("Record deleted");
            if (_revealing == target.Id)
            {
                /* The reveal's record is gone, and the operator knows why: the grid goes back to what it showed. */
                EndReveal();
            }

            await LoadAsync();
            _focusRow = RowAt(Math.Max(at, 0));
            return true;
        }
        catch (Exception exception)
        {
            RefusedWrite(exception);
            _conflicted = RecordVersion.IsConflict(_problem.Current) ? target.Id : null;
            return false;
        }
    }

    /// <summary>The row now at <paramref name="at"/>, or the last one when the deleted row was last; none when empty.</summary>
    private RowFocus? RowAt(int at)
        => _page is { Items.Count: > 0 } page
           && RefLabels.IdOf(page.Items[Math.Min(at, page.Items.Count - 1)][AlvoManagedColumns.Id]) is { } id
            ? new RowFocus(id)
            : null;

    /// <summary>
    /// With no row left to take focus, it goes to New record, the one action the empty page offers — after the confirm
    /// has closed, so its own focus return does not come last.
    /// </summary>
    private async Task FocusNewRecordAsync()
    {
        StateHasChanged();
        if (_newRecord is not null)
        {
            await _newRecord.FocusAsync();
        }
    }

    /// <summary>A refused delete: the panel on the page, which takes focus, and a new one for every refusal.</summary>
    private void RefusedWrite(Exception exception)
    {
        var problem = AdminProblem.From(exception, Logger, ProblemSite.RecordWrite);
        _writeRefused = true;
        _problem.Show(problem, title: RecordVersion.IsConflict(problem) ? RecordVersion.ConflictTitle : null);
    }

    /// <summary>What a reveal replaced: the search, the cursor of the page on screen, and the cursors that led to it.</summary>
    /// <param name="Search">The search term.</param>
    /// <param name="Cursor">The page's cursor, or <see langword="null"/> for the first page.</param>
    /// <param name="Cursors">The cursors of the pages before it, oldest first.</param>
    private sealed record GridView(string Search, string? Cursor, IReadOnlyList<string> Cursors);

    /// <summary>What the operator asked to delete, held while the confirm is on screen.</summary>
    /// <param name="Id">The record's id.</param>
    /// <param name="Label">What the confirm calls it.</param>
    /// <param name="Version">The version the record was opened with, which the delete carries (item 25).</param>
    private sealed record PendingDelete(Guid Id, string Label, AlvoPrecondition? Version);
}
