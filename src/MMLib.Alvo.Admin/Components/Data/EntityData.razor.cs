using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
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

    private readonly List<string> _cursors = [];
    private SchemaModel? _schema;
    private EntitySchema? _entity;
    private string _descriptor = string.Empty;
    private FieldMasks _masks = FieldMasks.None;
    private IReadOnlyList<FieldSchema> _columns = [];
    private RowLabel? _label;
    private RecordFormScope? _scope;
    private string? _status;
    private IReadOnlyDictionary<string, RowLabel> _targets = new Dictionary<string, RowLabel>(StringComparer.Ordinal);
    private IReadOnlyList<string> _searchable = [];
    private IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, string>> _labels
        = new Dictionary<string, IReadOnlyDictionary<Guid, string>>(StringComparer.Ordinal);
    private AlvoPage? _page;
    private AlvoContext? _context;
    private Dictionary<string, object?>? _form;
    private Guid? _editing;
    private AdminProblem? _problem;
    private string? _cursor;
    private string? _opened;
    private string _search = string.Empty;
    private GridSort? _sort;
    private int _searchVersion;
    private int _loadVersion;
    private bool _unreachable;
    private string _who = string.Empty;
    private bool _loading = true;

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
    private RecordGridScope GridScope
        => new(_page!, _columns, _masks, _label, _labels, _sort, HasPrevious: _cursors.Count > 0,
            SheetOpen: _form is not null);

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
        _problem = null;
        _opened = EntityName;
        _search = string.Empty;
        _sort = null;
        _page = null;
        _status = null;
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
            _schema, _label, _masks, DescriptorLens.Locks(_descriptor, EntityName), _targets, Report);
    }

    /// <summary>What the record form says after a write, shown above the grid until dismissed or replaced.</summary>
    private void Report(string status) => _status = status;

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
        var version = ++_loadVersion;
        try
        {
            var query = GridQuery.Page(
                _entity!, GridQuery.Search(_searchable, _search), _sort, PageSize, _cursor);
            var context = await Records.ContextAsync(CancellationToken.None);
            var page = await Records.PageAsync(query, context, CancellationToken.None);
            var labels = await LabelsAsync(page, context);
            if (version != _loadVersion)
            {
                return;
            }

            _page = page;
            _labels = labels;
            _problem = null;
        }
        catch (Exception exception)
        {
            if (version == _loadVersion)
            {
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
        => _problem = AdminProblem.From(exception, Logger, Site);

    /// <summary>
    /// A scoped entity read with no tenant is refused by the tenant guard, and the fix says so rather than
    /// sending the operator to the entity's rules.
    /// </summary>
    private ProblemSite Site
        => _entity?.Tenancy == TenancyMode.Scoped && _context?.Tenant is null
            ? ProblemSite.ScopedRecordsWithoutTenant
            : ProblemSite.Records;

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

    private void ResetPaging()
    {
        _cursors.Clear();
        _cursor = null;
    }

    private async Task Next()
    {
        if (_page?.NextCursor is { Length: > 0 } next)
        {
            _cursors.Add(_cursor ?? string.Empty);
            _cursor = next;
            await LoadAsync();
        }
    }

    private async Task Previous()
    {
        if (_cursors.Count > 0)
        {
            _cursor = _cursors[^1] is { Length: > 0 } previous ? previous : null;
            _cursors.RemoveAt(_cursors.Count - 1);
            await LoadAsync();
        }
    }

    private void NewRecord()
    {
        _status = null;
        _editing = null;
        _form = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private void Open(AlvoRecord record)
    {
        _status = null;
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
}
