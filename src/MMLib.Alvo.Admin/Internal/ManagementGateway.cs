using Microsoft.AspNetCore.Components.Authorization;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The dashboard's one reader over <see cref="IAlvoManagement"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a gateway rather than components injecting the contract directly.</b> Three of the
/// dashboard's screens need the descriptor, the schema and <c>capabilities</c> at once, and a
/// component that fetched each in its own <c>OnInitializedAsync</c> would make three round trips
/// per render pass and four when a child re-rendered. This holds one copy per circuit and
/// invalidates it on an apply — which is also the only moment any of them can have changed,
/// because every write to configuration goes through one route.
/// </para>
/// <para>
/// <b>It caches, and the cache is therefore a correctness question rather than a performance
/// one.</b> <see cref="Invalidate"/> runs after every real apply. A descriptor cannot change under
/// a dashboard that did not change it, except by another operator's apply — and that apply moves
/// the revision, so the next <c>If-Match</c> this circuit sends is refused with a concurrency
/// failure rather than silently overwriting them. The stale read is visible and safe.
/// </para>
/// <para>
/// <b>It authorizes nothing.</b> Every call runs with the operator's principal published on
/// <see cref="IAlvoContextAccessor"/>, and the core decides — see <see cref="AsOperatorAsync"/>.
/// </para>
/// </remarks>
/// <param name="management">The one management contract, resolved in-process (design §1.2).</param>
/// <param name="people">Administers membership, when a deployment has a membership store.</param>
/// <param name="callers">Turns the signed-in operator into the caller Alvo authorizes.</param>
/// <param name="authentication">Who is signed in on this circuit.</param>
/// <param name="ambient">Where a call publishes its caller for the core to read.</param>
internal sealed class ManagementGateway(
    IAlvoManagement management,
    IAlvoUserAdministration? people,
    IAlvoAdminCallerResolver callers,
    AuthenticationStateProvider authentication,
    IAlvoContextAccessor ambient)
{
    private AlvoPrincipal? _caller;
    private ManagementDescriptor? _descriptor;
    private SchemaModel? _schema;
    private ManagementCapabilities? _capabilities;
    private ManagementInfo? _info;
    private IReadOnlyList<ManagementProject>? _projects;

    /// <summary>The project this dashboard is looking at.</summary>
    /// <remarks>
    /// One project, and §2.6 says why: <c>GET {m}/projects</c> answers with exactly one today, so
    /// a switcher offering a choice would be offering a choice that does not exist. The property
    /// is here rather than inlined so that the day it becomes a choice, one place changes.
    /// </remarks>
    public string Project { get; private set; } = string.Empty;

    /// <summary>The descriptor as stored, with the revision it is at.</summary>
    public async ValueTask<ManagementDescriptor> DescriptorAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return _descriptor ??= await AsOperatorAsync(
            () => management.GetDescriptorAsync(project, ct)).ConfigureAwait(false);
    }

    /// <summary>The resolved schema — what the Data API actually serves.</summary>
    public async ValueTask<SchemaModel> SchemaAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return _schema ??= await AsOperatorAsync(
            () => management.GetSchemaAsync(project, ct)).ConfigureAwait(false);
    }

    /// <summary>What this build honours, warns about and refuses.</summary>
    public async ValueTask<ManagementCapabilities> CapabilitiesAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return _capabilities ??= await AsOperatorAsync(
            () => management.GetCapabilitiesAsync(project, ct)).ConfigureAwait(false);
    }

    /// <summary>Build, mode, data provider and startup mode.</summary>
    public async ValueTask<ManagementInfo> InfoAsync(CancellationToken ct)
        => _info ??= await AsOperatorAsync(() => management.GetInfoAsync(ct)).ConfigureAwait(false);

    /// <summary>Every project this build manages — one, today (§2.6).</summary>
    public async ValueTask<IReadOnlyList<ManagementProject>> ProjectsAsync(CancellationToken ct)
        => _projects ??= await AsOperatorAsync(
            () => management.ListProjectsAsync(ct)).ConfigureAwait(false);

    /// <summary>The append-only configuration history, newest first.</summary>
    /// <remarks>Never cached: it is the one read whose whole purpose is to be current.</remarks>
    public async Task<IReadOnlyList<ManagementRevision>> RevisionsAsync(CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return await AsOperatorAsync(
            () => management.ListRevisionsAsync(project, ct)).ConfigureAwait(false);
    }

    /// <summary>One past revision, with the descriptor it applied.</summary>
    public async Task<ManagementRevisionDetail> RevisionAsync(int revision, CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return await AsOperatorAsync(
            () => management.GetRevisionAsync(project, revision, ct)).ConfigureAwait(false);
    }

    /// <summary>The engine's verdict for one caller, one entity and one operation.</summary>
    public async Task<ManagementPolicyVerdict> SimulateAsync(
        ManagementPolicySimulation simulation, CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        return await AsOperatorAsync(
            () => management.SimulatePolicyAsync(project, simulation, ct)).ConfigureAwait(false);
    }

    /// <summary>Applies a descriptor, or plans one when <paramref name="dryRun"/> is set.</summary>
    /// <remarks>
    /// A real apply invalidates every cached read, because every one of them can have moved. A dry
    /// run invalidates nothing, because by its own contract it wrote nothing.
    /// </remarks>
    public async Task<ManagementApplyResult> ApplyAsync(
        string descriptorJson, int expectedRevision, bool allowDestructive, bool dryRun,
        string? author, string? reason, CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        var request = new ManagementApplyRequest(
            descriptorJson, expectedRevision, allowDestructive, dryRun, author, reason);

        var result = await AsOperatorAsync(
            () => management.ApplyDescriptorAsync(project, request, ct)).ConfigureAwait(false);

        if (!dryRun)
        {
            Invalidate();
        }

        return result;
    }

    /// <summary>
    /// Restores a past revision by appending its reverse migration as a new revision.
    /// </summary>
    /// <remarks>
    /// Two revision numbers, and confusing them is the bug this signature exists to make hard:
    /// <paramref name="targetRevision"/> is the past state to restore, while
    /// <paramref name="expectedRevision"/> is the revision the caller believes is <em>current</em>
    /// — the same optimistic-concurrency token an apply sends, and the reason a rollback issued
    /// against a history somebody else has moved is refused rather than silently reordered.
    /// </remarks>
    public async Task<ManagementApplyResult> RollbackAsync(
        int targetRevision, int expectedRevision, bool allowDestructive, bool dryRun,
        string? author, string? reason, CancellationToken ct)
    {
        var project = await ProjectAsync(ct).ConfigureAwait(false);
        var request = new ManagementRollbackRequest(
            expectedRevision, allowDestructive, dryRun, author, reason);

        var result = await AsOperatorAsync(
            () => management.RollbackAsync(project, targetRevision, request, ct)).ConfigureAwait(false);

        if (!dryRun)
        {
            Invalidate();
        }

        return result;
    }

    /// <summary>
    /// Who to record as the author of a change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every apply this dashboard makes carries one.</b> A revision's <c>Author</c> is the only
    /// place a configuration change is attributed, Configuration history renders it forever, and
    /// §3.7 leans on exactly that when it argues that the recorded route is the one an
    /// administrator should prefer. An apply sent with no author reads as <i>code-first or
    /// system</i> — which is true of a mounted descriptor and false of a person clicking Apply.
    /// </para>
    /// <para>
    /// The address, because that is what another operator recognises; the subject id when the
    /// host's authentication did not supply one, because an opaque id is still better than nothing.
    /// </para>
    /// </remarks>
    public async ValueTask<string?> AuthorAsync()
    {
        var state = await authentication.GetAuthenticationStateAsync().ConfigureAwait(false);
        var name = state.User.Identity?.Name;

        return name is { Length: > 0 }
            ? name
            : state.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }

    /// <summary>Whether this deployment can administer people at all.</summary>
    /// <remarks>
    /// <see langword="false"/> when no package registered a membership store. The Access screen
    /// then says so rather than offering controls that would throw — the same rule as a refused
    /// feature: a control whose only possible outcome is a refusal is worse than its absence.
    /// </remarks>
    public bool CanAdministerPeople => people is not null;

    /// <summary>One page of the people on this project.</summary>
    public Task<AlvoUserPage> PeopleAsync(AlvoUserQuery query, CancellationToken ct)
        => AsOperatorAsync(() => Administration.ListAsync(query, ct));

    /// <summary>Creates a person who can sign in, once somebody sets their password.</summary>
    public Task<AlvoUser> CreatePersonAsync(AlvoUserCreation creation, CancellationToken ct)
        => AsOperatorAsync(() => Administration.CreateAsync(creation, ct));

    /// <summary>Replaces a person's roles.</summary>
    public Task<AlvoUser> SetRolesAsync(UserId user, IReadOnlyList<string> roleNames, CancellationToken ct)
        => AsOperatorAsync(() => Administration.SetRolesAsync(user, roleNames, ct));

    /// <summary>Grants, changes or removes the one tenant a person acts in.</summary>
    public Task<AlvoUser> SetTenantAsync(UserId user, TenantId? tenant, CancellationToken ct)
        => AsOperatorAsync(() => Administration.SetTenantAsync(user, tenant, ct));

    /// <summary>Bars a person from signing in, or lets them back.</summary>
    public Task<AlvoUser> SetDisabledAsync(UserId user, bool disabled, CancellationToken ct)
        => AsOperatorAsync(() => Administration.SetDisabledAsync(user, disabled, ct));

    /// <summary>Mints the single-use token with which somebody sets their own password.</summary>
    public Task<AlvoCredentialToken> IssueCredentialTokenAsync(UserId user, CancellationToken ct)
        => AsOperatorAsync(() => Administration.IssueCredentialTokenAsync(user, ct));

    private IAlvoUserAdministration Administration => people
        ?? throw new InvalidOperationException(
            "This deployment registered no membership store, so there is nobody to administer. "
            + "CanAdministerPeople says so before a screen offers a control.");

    /// <summary>Drops every cached read.</summary>
    public void Invalidate()
    {
        _descriptor = null;
        _schema = null;
        _capabilities = null;
    }

    /// <summary>
    /// Runs one management call as the signed-in operator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole of the dashboard's authorization story, and it is deliberately not its
    /// own.</b> The management service reads its caller from <see cref="IAlvoContextAccessor"/> —
    /// the same ambient principal the HTTP route filter publishes before every management request.
    /// Publishing it here rather than checking a level inside a component is what makes <i>"the
    /// dashboard is not a second authorization system"</i> true by construction: the screen asks,
    /// the engine refuses, and the refusal arrives as the same exception an HTTP caller gets.
    /// </para>
    /// <para>
    /// <b>Per call rather than once per circuit.</b> A Blazor event handler runs on its own
    /// execution context, so an ambient value set at circuit start is not reliably still there
    /// when a button is clicked twenty minutes later. The principal itself is resolved once and
    /// cached; what repeats is only publishing it.
    /// </para>
    /// <para>
    /// The previous value is restored rather than cleared, because during a statically rendered
    /// pass this runs inside an HTTP request that may already have published one.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">What the call answers with.</typeparam>
    /// <param name="call">The management call.</param>
    /// <returns>Whatever the call answered.</returns>
    private async Task<T> AsOperatorAsync<T>(Func<Task<T>> call)
    {
        var previous = ambient.Principal;
        ambient.Principal = await CallerAsync().ConfigureAwait(false);

        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            ambient.Principal = previous;
        }
    }

    /// <summary>
    /// The operator's caller, resolved once per circuit.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> answer is left as <see langword="null"/>: the core then sees an
    /// unauthenticated call and refuses it with <see cref="ManagementForbiddenException"/>, which
    /// is the screen an operator whose account was disabled mid-session should be looking at.
    /// </remarks>
    private async ValueTask<AlvoPrincipal?> CallerAsync()
    {
        if (_caller is not null)
        {
            return _caller;
        }

        var state = await authentication.GetAuthenticationStateAsync().ConfigureAwait(false);
        _caller = await callers.ResolveAsync(state.User, CancellationToken.None).ConfigureAwait(false);
        return _caller;
    }

    private async ValueTask<string> ProjectAsync(CancellationToken ct)
    {
        if (Project.Length > 0)
        {
            return Project;
        }

        var projects = await ProjectsAsync(ct).ConfigureAwait(false);
        Project = projects.Count > 0 ? projects[0].Name : string.Empty;
        return Project;
    }
}
